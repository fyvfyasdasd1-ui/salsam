(function () {
  const currentEl = document.getElementById('current');
  const historyEl = document.getElementById('history');

  const state = {
    current: '0',
    previous: null,
    operator: null,
    justEvaluated: false,
  };

  const OP_SYMBOL = { '+': '+', '-': '−', '*': '×', '/': '÷' };

  function formatNumber(value) {
    if (value === '' || value === '-' || value === null) return '0';
    const num = Number(value);
    if (!isFinite(num)) return 'Ошибка';
    const str = typeof value === 'string' ? value : String(num);
    if (str.includes('.') || str.endsWith('.')) {
      const [intPart, decPart] = str.split('.');
      const formattedInt = Number(intPart).toLocaleString('ru-RU');
      return decPart !== undefined ? `${formattedInt}.${decPart}` : `${formattedInt}.`;
    }
    return num.toLocaleString('ru-RU', { maximumFractionDigits: 10 });
  }

  function render() {
    currentEl.textContent = formatNumber(state.current);
    if (state.operator && state.previous !== null) {
      historyEl.textContent = `${formatNumber(state.previous)} ${OP_SYMBOL[state.operator]}`;
    } else {
      historyEl.textContent = '';
    }
    document.querySelectorAll('.key-op').forEach((btn) => {
      btn.classList.toggle('active', !state.justEvaluated && state.operator === btn.dataset.op && state.current === '0');
    });
  }

  function inputDigit(d) {
    if (state.justEvaluated) {
      state.current = d;
      state.previous = null;
      state.operator = null;
      state.justEvaluated = false;
      return;
    }
    if (state.current === '0') state.current = d;
    else if (state.current.replace(/[^0-9]/g, '').length < 14) state.current += d;
  }

  function inputDot() {
    if (state.justEvaluated) {
      state.current = '0.';
      state.previous = null;
      state.operator = null;
      state.justEvaluated = false;
      return;
    }
    if (!state.current.includes('.')) state.current += '.';
  }

  function clearAll() {
    state.current = '0';
    state.previous = null;
    state.operator = null;
    state.justEvaluated = false;
  }

  function toggleSign() {
    if (state.current === '0') return;
    state.current = state.current.startsWith('-')
      ? state.current.slice(1)
      : '-' + state.current;
  }

  function percent() {
    const value = parseFloat(state.current) / 100;
    state.current = String(value);
    state.justEvaluated = true;
  }

  function compute(a, b, op) {
    switch (op) {
      case '+': return a + b;
      case '-': return a - b;
      case '*': return a * b;
      case '/': return b === 0 ? NaN : a / b;
    }
    return b;
  }

  function setOperator(op) {
    const value = parseFloat(state.current);
    if (state.previous !== null && state.operator && !state.justEvaluated) {
      const result = compute(state.previous, value, state.operator);
      state.previous = result;
      state.current = String(result);
    } else {
      state.previous = value;
    }
    state.operator = op;
    state.justEvaluated = false;
    state.current = '0';
  }

  function equals() {
    if (state.operator === null || state.previous === null) return;
    const value = parseFloat(state.current === '0' && !state.justEvaluated ? state.previous : state.current);
    const result = compute(state.previous, value, state.operator);
    historyEl.textContent = `${formatNumber(state.previous)} ${OP_SYMBOL[state.operator]} ${formatNumber(value)} =`;
    state.current = String(result);
    state.previous = null;
    state.operator = null;
    state.justEvaluated = true;
    currentEl.textContent = formatNumber(state.current);
    return true;
  }

  document.querySelectorAll('.key').forEach((btn) => {
    btn.addEventListener('click', () => {
      const { action, value, op } = btn.dataset;
      switch (action) {
        case 'digit':   inputDigit(value); break;
        case 'dot':     inputDot(); break;
        case 'clear':   clearAll(); break;
        case 'sign':    toggleSign(); break;
        case 'percent': percent(); break;
        case 'op':      setOperator(op); break;
        case 'equals':
          if (equals()) return;
          break;
      }
      render();
    });
  });

  document.addEventListener('keydown', (e) => {
    if (e.key >= '0' && e.key <= '9') inputDigit(e.key);
    else if (e.key === '.' || e.key === ',') inputDot();
    else if (['+', '-', '*', '/'].includes(e.key)) setOperator(e.key);
    else if (e.key === 'Enter' || e.key === '=') { e.preventDefault(); if (equals()) return; }
    else if (e.key === 'Escape') clearAll();
    else if (e.key === 'Backspace') {
      if (state.justEvaluated) clearAll();
      else if (state.current.length > 1) state.current = state.current.slice(0, -1);
      else state.current = '0';
    } else return;
    render();
  });

  render();
})();
