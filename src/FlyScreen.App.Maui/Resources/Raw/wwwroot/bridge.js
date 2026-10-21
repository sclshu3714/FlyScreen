/* FlyScreen ↔ Monaco：源码拉取、行数上限、智能提示 */
(function () {
  let editor = null;
  let bpDecorations = [];
  let breakpoints = [];
  let bootstrapped = false;
  let maxLines = 1000;
  let keywordMap = {};
  let completionDisposable = null;

  window.flyEditorReady = false;

  /** 跨屏插入是否应拉起补全（对齐桌面 AvalonEdit：字母 / _ / .） */
  function shouldTriggerSuggest(text) {
    if (!text) return false;
    for (var i = 0; i < text.length; i++) {
      var c = text.charAt(i);
      if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c === '_' || c === '.')
        return true;
    }
    return false;
  }

  function triggerSuggestSoon() {
    if (!editor) return;
    // type 之后下一帧再触发，确保 model/光标已更新
    setTimeout(function () {
      try {
        editor.trigger('keyboard', 'editor.action.triggerSuggest', {});
      } catch (e) { /* ignore */ }
    }, 0);
  }

  /** 跨屏键盘：往 Monaco 插入文本（WebView 无障碍写不进编辑器） */
  window.flyType = function (text) {
    if (!editor || text == null || text === '') return false;
    try {
      editor.focus();
      var s = String(text);
      editor.trigger('keyboard', 'type', { text: s });
      reportCaret();
      // 程序化 type 不会走真实键盘，quickSuggestions 常不弹；显式拉起智能提示
      if (shouldTriggerSuggest(s))
        triggerSuggestSoon();
      return true;
    } catch (e) { return false; }
  };

  /** 跨屏键盘：特殊键 */
  window.flyEditorAction = function (name) {
    if (!editor) return false;
    try {
      editor.focus();
      switch (String(name || '')) {
        case 'backspace': editor.trigger('keyboard', 'deleteLeft', null); break;
        case 'delete': editor.trigger('keyboard', 'deleteRight', null); break;
        case 'enter': editor.trigger('keyboard', 'type', { text: '\n' }); break;
        case 'left': editor.trigger('keyboard', 'cursorLeft', null); break;
        case 'right': editor.trigger('keyboard', 'cursorRight', null); break;
        case 'up': editor.trigger('keyboard', 'cursorUp', null); break;
        case 'down': editor.trigger('keyboard', 'cursorDown', null); break;
        case 'home': editor.trigger('keyboard', 'cursorHome', null); break;
        case 'end': editor.trigger('keyboard', 'cursorEnd', null); break;
        case 'tab': editor.trigger('keyboard', 'tab', null); break;
        default: return false;
      }
      reportCaret();
      return true;
    } catch (e) { return false; }
  };

  function post(obj) {
    try {
      if (window.HybridWebView && typeof HybridWebView.SendRawMessage === 'function')
        HybridWebView.SendRawMessage(JSON.stringify(obj));
    } catch (e) { /* ignore */ }
  }

  function lineCountOf(text) {
    if (!text) return 1;
    var n = 1;
    for (var i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) n++;
    return n;
  }

  function setBreakpoints(lines) {
    breakpoints = (lines || []).map(Number).filter(n => n >= 1);
    if (!editor) return;
    const decs = breakpoints.map(line => ({
      range: new monaco.Range(line, 1, line, 1),
      options: {
        isWholeLine: true,
        glyphMarginClassName: 'fly-bp-glyph',
        linesDecorationsClassName: 'fly-bp-line'
      }
    }));
    bpDecorations = editor.deltaDecorations(bpDecorations, decs);
  }

  function toggleBreakpoint(line) {
    const i = breakpoints.indexOf(line);
    if (i >= 0) breakpoints.splice(i, 1);
    else breakpoints.push(line);
    breakpoints.sort((a, b) => a - b);
    setBreakpoints(breakpoints);
    post({ type: 'breakpointToggled', line: line, breakpoints: breakpoints.slice() });
  }

  function registerCompletion(language) {
    if (completionDisposable) {
      try { completionDisposable.dispose(); } catch (e) { /* ignore */ }
      completionDisposable = null;
    }
    var words = keywordMap[language] || keywordMap[language && language.toLowerCase()] || [];
    if (!words.length || !language || language === 'plaintext') return;

    // 字母也作为 trigger，便于本机软键盘；跨屏仍靠 flyType 里显式 triggerSuggest
    var letterTriggers = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_'.split('');
    completionDisposable = monaco.languages.registerCompletionItemProvider(language, {
      triggerCharacters: ['.'].concat(letterTriggers),
      provideCompletionItems: function (model, position) {
        var word = model.getWordUntilPosition(position);
        var range = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn
        };
        var prefix = (word.word || '').toLowerCase();
        var doc = model.getValue();
        var fromDoc = {};
        var re = /[A-Za-z_][A-Za-z0-9_]{1,}/g;
        var m;
        while ((m = re.exec(doc))) {
          if (!prefix || m[0].toLowerCase().indexOf(prefix) === 0)
            fromDoc[m[0]] = true;
        }
        words.forEach(function (w) {
          if (!prefix || w.toLowerCase().indexOf(prefix) === 0)
            fromDoc[w] = true;
        });
        var suggestions = Object.keys(fromDoc).slice(0, 80).map(function (label) {
          return {
            label: label,
            kind: monaco.languages.CompletionItemKind.Keyword,
            insertText: label,
            range: range
          };
        });
        return { suggestions: suggestions };
      }
    });
  }

  function applyTheme(theme, background) {
    try {
      if (theme) monaco.editor.setTheme(theme);
      var dark = theme === 'vs-dark';
      var bg = background || (dark ? '#0F172A' : '#FFFFFF');
      var gutter = dark ? '#1E293B' : '#F3F4F6';
      document.documentElement.style.background = bg;
      document.body.style.background = bg;
      var el = document.getElementById('container');
      if (el) el.style.background = bg;
      var styleId = 'fly-editor-gutter-style';
      var st = document.getElementById(styleId);
      if (!st) {
        st = document.createElement('style');
        st.id = styleId;
        document.head.appendChild(st);
      }
      st.textContent =
        '.monaco-editor .margin, .monaco-editor .glyph-margin { background-color: ' + gutter + ' !important; }';
    } catch (e) { /* ignore */ }
  }

  function applyBootstrap(data) {
    if (!editor || !data) return;
    const value = data.value != null ? String(data.value)
      : (data.Value != null ? String(data.Value) : '');
    const language = data.language || data.Language || 'plaintext';
    const bps = data.breakpoints || data.Breakpoints || [];
    if (data.maxLines || data.MaxLines)
      maxLines = Number(data.maxLines || data.MaxLines) || 1000;
    var kw = data.keywords || data.Keywords;
    if (kw && kw.length) {
      keywordMap[language] = kw;
      keywordMap[language.toLowerCase()] = kw;
    }
    var theme = data.theme || data.Theme || 'vs';
    var background = data.background || data.Background;
    applyTheme(theme, background);
    editor.setValue(value);
    try { monaco.editor.setModelLanguage(editor.getModel(), language); } catch (e) { /* ignore */ }
    registerCompletion(language);
    setBreakpoints(bps);
    bootstrapped = true;
    reportCaret();
  }

  function reportCaret() {
    if (!editor) return;
    var pos = editor.getPosition() || { lineNumber: 1, column: 1 };
    var caretX = 0, caretY = 0;
    try {
      // 相对编辑器可视区；再加上 container 在 WebView 中的位置
      var sc = editor.getScrolledVisiblePosition(pos);
      var el = document.getElementById('container');
      var rect = el ? el.getBoundingClientRect() : { left: 0, top: 0 };
      if (sc) {
        caretX = rect.left + sc.left;
        caretY = rect.top + sc.top + (sc.height || 16);
      }
    } catch (e) { /* ignore */ }
    post({
      type: 'caret',
      line: pos.lineNumber,
      column: pos.column,
      lineCount: editor.getModel() ? editor.getModel().getLineCount() : 1,
      caretX: caretX,
      caretY: caretY
    });
  }

  window.flyReportCaret = reportCaret;

  function handleHostMessage(raw) {
    let msg;
    if (typeof raw === 'object' && raw !== null) msg = raw;
    else {
      try { msg = JSON.parse(raw); } catch { return; }
    }
    if (!msg || !msg.type) return;

    switch (msg.type) {
      case 'setValue':
      case 'init':
        applyBootstrap(msg);
        break;
      case 'getValue':
        post({ type: 'value', requestId: msg.requestId, value: editor ? editor.getValue() : '' });
        break;
      case 'setLanguage':
        if (editor && msg.language) {
          monaco.editor.setModelLanguage(editor.getModel(), msg.language);
          if (msg.keywords) {
            keywordMap[msg.language] = msg.keywords;
            registerCompletion(msg.language);
          }
        }
        break;
      case 'setBreakpoints':
        setBreakpoints(msg.breakpoints || []);
        break;
      case 'clearBreakpoints':
        setBreakpoints([]);
        break;
      case 'setTheme':
        applyTheme(msg.theme || msg.Theme, msg.background || msg.Background);
        break;
    }
  }

  window.flyHandleHostMessage = handleHostMessage;
  window.flyGetValue = function () { return editor ? editor.getValue() : ''; };
  window.flySetValue = function (v) {
    if (editor) editor.setValue(v == null ? '' : String(v));
  };
  window.flyLineCount = function () {
    return editor && editor.getModel() ? editor.getModel().getLineCount() : 1;
  };

  window.addEventListener('HybridWebViewMessageReceived', function (e) {
    let raw = null;
    if (e && e.detail != null) {
      if (typeof e.detail === 'string') raw = e.detail;
      else if (e.detail.message != null) raw = e.detail.message;
    }
    if (raw) handleHostMessage(raw);
  });

  async function pullBootstrapFromDotNet() {
    if (bootstrapped || !editor) return;
    for (let i = 0; i < 30; i++) {
      try {
        if (window.HybridWebView && typeof HybridWebView.InvokeDotNet === 'function') {
          const data = await HybridWebView.InvokeDotNet('GetBootstrap');
          if (data) {
            applyBootstrap(typeof data === 'string' ? JSON.parse(data) : data);
            if (bootstrapped) {
              post({ type: 'ready' });
              return;
            }
          }
        }
      } catch (e) { /* 目标尚未 SetInvokeJavaScriptTarget */ }
      await new Promise(r => setTimeout(r, 200));
    }
    post({ type: 'ready' });
  }

  require.config({ paths: { vs: 'vs' } });
  require(['vs/editor/editor.main'], function () {
    const style = document.createElement('style');
    // 手机屏窄：压缩断点栏 + 行号宽度
    style.textContent =
      '.fly-bp-glyph { background: #ef4444; border-radius: 50%; width: 8px !important; height: 8px !important; margin-left: 2px !important; margin-top: 3px !important; }' +
      '.fly-bp-line { background: rgba(239,68,68,0.08); }' +
      '.monaco-editor .glyph-margin { width: 14px !important; min-width: 14px !important; }' +
      '.monaco-editor .margin-view-overlays .line-numbers { padding: 0 2px 0 0 !important; min-width: 0 !important; }';
    document.head.appendChild(style);

    editor = monaco.editor.create(document.getElementById('container'), {
      value: '',
      language: 'plaintext',
      theme: 'vs',
      automaticLayout: true,
      minimap: { enabled: false },
      fontSize: 13,
      fontFamily: 'Consolas, Cascadia Mono, Menlo, monospace',
      glyphMargin: true,
      lineNumbers: 'on',
      lineNumbersMinChars: 2,
      lineDecorationsWidth: 4,
      folding: false,
      scrollBeyondLastLine: false,
      wordWrap: 'off',
      tabSize: 4,
      insertSpaces: true,
      quickSuggestions: { other: true, comments: false, strings: true },
      suggestOnTriggerCharacters: true,
      wordBasedSuggestions: 'currentDocument',
      snippetSuggestions: 'inline',
      suggest: { showKeywords: true, showSnippets: true, preview: true }
    });

    editor.onMouseDown(function (e) {
      if (!e.target) return;
      var t = e.target.type;
      if (t === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN
          || t === monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS
          || t === monaco.editor.MouseTargetType.GUTTER_LINE_DECORATIONS) {
        var line = e.target.position && e.target.position.lineNumber;
        if (line) toggleBreakpoint(line);
      }
    });

    editor.onDidChangeCursorPosition(function () { reportCaret(); });
    editor.onDidChangeModelContent(function () { reportCaret(); });

    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, function () {
      var text = editor.getValue();
      var n = lineCountOf(text);
      if (n > maxLines) {
        post({ type: 'lineLimitExceeded', lineCount: n, maxLines: maxLines, value: text });
        return;
      }
      post({ type: 'saveRequested', value: text });
    });

    // Shift+Alt+F：请求宿主按语言格式化
    editor.addCommand(monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF, function () {
      post({ type: 'formatRequested', value: editor.getValue() });
    });

    window.flyEditorReady = true;
    pullBootstrapFromDotNet();
  });
})();
