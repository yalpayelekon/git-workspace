'use strict';
const sessions = new Map();
const send = message => window.chrome.webview.postMessage(message);
let active;
function activate(id) {
  active = id;
  for (const [key, session] of sessions) {
    session.element.classList.toggle('active', key === id);
    session.button.classList.toggle('active', key === id);
  }
  const session = sessions.get(id);
  if (session) { session.fit.fit(); session.terminal.focus(); }
}
function fitAll() { for (const session of sessions.values()) session.fit.fit(); }
function serializeBuffer(buffer) {
  const lines = [];
  for (let i = 0; i < buffer.length; i++) {
    const line = buffer.getLine(i);
    const next = i + 1 < buffer.length ? buffer.getLine(i + 1) : null;
    // Keep wrap-boundary cells intact; only trim the end of a logical line.
    const text = line ? line.translateToString(!(next && next.isWrapped)) : '';
    if (line && line.isWrapped && lines.length) lines[lines.length - 1] += text;
    else lines.push(text);
  }
  while (lines.length && !lines[lines.length - 1].trim()) lines.pop();
  return lines.join('\n');
}
function bufferText(terminal) {
  // Always keep normal scrollback. When an app is on the alternate screen,
  // active alone would drop history that still lives in buffer.normal.
  const sections = [serializeBuffer(terminal.buffer.normal)];
  if (terminal.buffer.active.type === 'alternate') {
    const alternate = serializeBuffer(terminal.buffer.alternate);
    if (alternate) sections.push(alternate);
  }
  return sections.filter(section => section.length).join('\n\n');
}
document.getElementById('new').addEventListener('click', () => send({ type:'new' }));
document.getElementById('save').addEventListener('click', () => {
  const session = active != null ? sessions.get(active) : null;
  if (!session) return;
  send({
    type: 'save',
    id: active,
    title: session.title || `PowerShell ${active}`,
    data: bufferText(session.terminal),
  });
});
new ResizeObserver(fitAll).observe(document.getElementById('terminals'));
window.chrome.webview.addEventListener('message', event => {
  const m = event.data;
  if (m.type === 'created') {
    const element = document.createElement('div'); element.className = 'session';
    document.getElementById('terminals').appendChild(element);
    const button = document.createElement('button'); button.textContent = m.title;
    const close = document.createElement('span'); close.textContent = '×'; close.className = 'close'; close.title = 'Close session and its processes';
    close.addEventListener('click', e => { e.stopPropagation(); send({ type:'close', id:m.id }); });
    button.appendChild(close); button.addEventListener('click', () => activate(m.id));
    document.getElementById('sessions').insertBefore(button, document.getElementById('new'));
    const terminal = new Terminal({ cursorBlink:true, scrollback:5000, fontSize:13, fontFamily:'Consolas, monospace', theme:{ background:'#1e1e1e', foreground:'#e0e0e0', cursor:'#ffffff', selectionBackground:'#264f78', selectionForeground:'#ffffff', black:'#9a9a9a', brightBlack:'#b0b0b0', red:'#f48771', green:'#89d185', yellow:'#dcdcaa', blue:'#75beff', magenta:'#d8a0df', cyan:'#7fd5dc', white:'#e0e0e0', brightRed:'#ffb0a0', brightGreen:'#b5e8a8', brightYellow:'#ffffb0', brightBlue:'#a4d6ff', brightMagenta:'#efbfff', brightCyan:'#b0f0f0', brightWhite:'#ffffff' }, allowProposedApi:false });
    const fit = new FitAddon.FitAddon(); terminal.loadAddon(fit); terminal.open(element);
    terminal.onData(data => {
      for (let offset = 0; offset < data.length;) {
        let end = Math.min(offset + 16384, data.length);
        if (end < data.length && data.charCodeAt(end - 1) >= 0xd800 && data.charCodeAt(end - 1) <= 0xdbff) end--;
        send({ type:'input', id:m.id, data:data.slice(offset, end) }); offset = end;
      }
    });
    terminal.onResize(size => send({ type:'resize', id:m.id, columns:size.cols, rows:size.rows }));
    // Use the same host clipboard path for mouse and keyboard paste. Browser clipboard
    // permissions/availability must not make the two gestures behave differently.
    const pasteFromClipboard = () => send({ type:'paste-request', id:m.id });
    terminal.attachCustomKeyEventHandler(e => {
      if (e.type === 'keydown' && e.ctrlKey && !e.altKey) {
        const key = e.shiftKey && e.code === 'KeyM' ? 'problems' : !e.shiftKey ? ({KeyJ:'panel',KeyB:'sidebar',Backquote:'terminal'})[e.code] : null;
        if (key) { send({type:'shortcut',key}); return false; }
      }
      if (e.type === 'keydown' && e.ctrlKey && e.shiftKey && e.code === 'KeyC') {
        navigator.clipboard.writeText(terminal.getSelection()); return false;
      }
      if (e.type === 'keydown' && e.ctrlKey && e.shiftKey && e.code === 'KeyV') {
        e.preventDefault(); pasteFromClipboard(); return false;
      }
      return true;
    });
    // Windows console convention: right-click pastes via the host clipboard (WebView menus are off).
    element.addEventListener('contextmenu', e => { e.preventDefault(); terminal.focus(); pasteFromClipboard(); });
    sessions.set(m.id, { element, button, terminal, fit, title: m.title }); activate(m.id);
  } else if (m.type === 'paste') {
    const session = sessions.get(m.id);
    if (session && m.data) { session.terminal.focus(); session.terminal.paste(m.data); }
  } else if (m.type === 'focus') {
    if (active != null) activate(active);
  } else if (m.type === 'output') {
    const session = sessions.get(m.id);
    if (session) session.terminal.write(m.data, () => send({ type:'ack', messageId:m.messageId }));
    else send({ type:'ack', messageId:m.messageId });
  } else if (m.type === 'closed') {
    const session = sessions.get(m.id);
    if (session) { session.terminal.dispose(); session.element.remove(); session.button.remove(); sessions.delete(m.id); }
    if (active === m.id && sessions.size) activate(sessions.keys().next().value);
  } else if (m.type === 'exit') {
    const session = sessions.get(m.id);
    if (session) session.terminal.write('\r\n[PowerShell exited. Use + to start a session.]\r\n');
  } else if (m.type === 'fit') fitAll();
});
send({ type:'ready' });
