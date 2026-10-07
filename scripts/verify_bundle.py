"""Check that a publish output is a self-contained Windows x64 WPF bundle."""
import argparse
import json
import struct
import zlib
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("exe", type=Path)
parser.add_argument("--native-host", type=Path, help="Optional trusted SDK singlefilehost.exe to compare native code")
args = parser.parse_args()
data = args.exe.read_bytes()


def sections(binary):
    assert binary[:2] == b"MZ", "Not a Windows executable"
    pe = struct.unpack_from("<I", binary, 0x3C)[0]
    assert binary[pe:pe + 4] == b"PE\0\0", "Invalid PE header"
    assert struct.unpack_from("<H", binary, pe + 4)[0] == 0x8664, "Expected x64"
    count = struct.unpack_from("<H", binary, pe + 6)[0]
    header_size = struct.unpack_from("<H", binary, pe + 20)[0]
    result = {}
    for index in range(count):
        start = pe + 24 + header_size + index * 40
        name = binary[start:start + 8].rstrip(b"\0")
        size, offset = struct.unpack_from("<II", binary, start + 16)
        result[name] = binary[offset:offset + size]
    return result


code = sections(data)
if args.native_host:
    assert code[b".text"] == sections(args.native_host.read_bytes())[b".text"], "Native runtime host code differs"

magic = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")
location = data.index(magic)
position = struct.unpack_from("<Q", data, location - 8)[0]
major, minor, count = struct.unpack_from("<IIi", data, position)
assert major == 6 and 0 < count < 5000, "Unexpected bundle format"
position += 12


def read_string():
    global position
    length = 0
    for shift in range(0, 35, 7):
        byte = data[position]
        position += 1
        length |= (byte & 127) << shift
        if byte < 128:
            break
    else:
        raise ValueError("Invalid bundle string")
    assert position + length <= len(data), "Truncated bundle string"
    value = data[position:position + length].decode("utf-8")
    position += length
    return value


read_string()  # bundle ID
position += 40  # deps/runtimeconfig offsets, sizes and flags
entries = {}
for _ in range(count):
    offset, size, compressed = struct.unpack_from("<qqq", data, position)
    position += 25  # includes file type byte
    name = read_string()
    assert offset >= 0 and size >= 0 and compressed >= 0
    assert offset + (compressed or size) <= len(data), "Truncated bundle entry"
    entries[name] = (offset, size, compressed)

for name in ("Salsam.dll", "Salsam.Core.dll", "System.Private.CoreLib.dll", "Microsoft.VisualBasic.Core.dll",
             "PresentationFramework.dll", "PresentationCore.dll", "wpfgfx_cor3.dll",
             "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "Salsam.runtimeconfig.json"):
    assert name in entries, "Missing embedded dependency: " + name

offset, size, compressed = entries["Salsam.runtimeconfig.json"]
raw = data[offset:offset + (compressed or size)]
if compressed:
    raw = zlib.decompress(raw, -15)
assert len(raw) == size
config = json.loads(raw)["runtimeOptions"]
assert {framework["name"] for framework in config["includedFrameworks"]} >= {
    "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}
assert "framework" not in config and "frameworks" not in config, "Output requires an external runtime"
print(f"PASS Windows x64 self-contained WPF bundle: {count} embedded entries, {len(data):,} bytes")
