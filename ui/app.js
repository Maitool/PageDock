/* 页坞 PageDock — 工作台逻辑（v0.4）
 * 窗口控制 / 搜索 / 添加 / 设置入口都在原生标签栏（TabStrip），本页只负责内容：
 * 应用卡片网格（紧凑图标卡 + 悬停操作条）、编辑/设置弹窗、悬停 2s 信息提示。
 * 宿主环境：通过 window.chrome.webview 与 C# 壳通信；
 * 浏览器预览：无宿主时走 Mock（localStorage），右下角显示“预览模式”。 */
(function () {
  'use strict';

  /* ---------------- 宿主桥 ---------------- */
  var hasBridge = !!(window.chrome && window.chrome.webview);
  var seq = 0;
  var waiters = {};

  if (hasBridge) {
    window.chrome.webview.addEventListener('message', function (e) {
      var d = e.data;
      if (!d) return;
      if (d.reply !== undefined && d.reply !== null) {
        var w = waiters[d.reply];
        if (w) { delete waiters[d.reply]; w(d); }
        return;
      }
      // 原生标签栏 → 页面事件
      if (d.event === 'search') {
        searchQ = String(d.q || '');
        render();
      } else if (d.event === 'open-settings') {
        // 宿主已保证先把首页切到前台，这里只需处理“已有别的弹窗”的情况，
        // 不能静默吞掉（那就是“点了没反应”）
        if (modalOpen()) { if (modalKind !== 'settings') toast('请先关闭当前弹窗，再打开设置'); }
        else settingsDialog(d.overlay === true);
      } else if (d.event === 'settings-solo-off') {
        // 宿主已在设置关闭并把首页 WebView 隐藏/复位后通知：撤销 solo（只留弹窗）模式
        document.documentElement.classList.remove('solo');
      } else if (d.event === 'open-add') {
        if (modalOpen()) { if (modalKind !== 'app') toast('请先关闭当前弹窗，再添加应用'); }
        else addDialog();
      } else if (d.event === 'widget-pos' && d.appId) {
        var wp = findApp(d.appId);
        if (wp) { wp.wx = d.x; wp.wy = d.y; wp.ww = d.w; wp.wh = d.h; save(); }
      } else if (d.event === 'widget-closed' && d.appId) {
        delete pinned[d.appId];
        var w0 = findApp(d.appId);
        // 关闭小组件 = 取消“钉在桌面”，下次启动不再自动还原（应用退出时的关闭由宿主屏蔽）
        if (w0) { w0.wx = d.x; w0.wy = d.y; w0.ww = d.w; w0.wh = d.h; w0.wp = false; save(); }
      } else if (d.event === 'child-size' && d.appId) {
        var a = findApp(d.appId);
        if (a) { a.w = d.w; a.h = d.h; save(); }
      } else if (d.event === 'toggle-topmost') {
        // 原生标题栏图钉翻转置顶：改设置、持久化，再回推宿主应用 TopMost
        state.settings.topmost = !(state.settings.topmost === true);
        save();
        fire('set-topmost', { on: state.settings.topmost });
        toast(state.settings.topmost ? '窗口已置顶' : '已取消置顶');
      }
    });
  }

  function call(op, data) {
    return new Promise(function (res) {
      if (!hasBridge) { Mock.call(op, data, res); return; }
      var rid = ++seq;
      waiters[rid] = res;
      var msg = { op: op, rid: rid };
      if (data) for (var k in data) msg[k] = data[k];
      window.chrome.webview.postMessage(msg);
    });
  }

  function fire(op, data) {
    if (!hasBridge) return;
    var msg = { op: op };
    if (data) for (var k in data) msg[k] = data[k];
    window.chrome.webview.postMessage(msg);
  }

  /* ---------------- Mock（浏览器预览用） ---------------- */
  var Mock = {
    call: function (op, data, res) {
      switch (op) {
        case 'get-state':
          res({ ok: true, stateJson: localStorage.getItem('pd-state'), dataDir: '（预览）', exeDir: '（预览）' });
          break;
        case 'save-state':
          localStorage.setItem('pd-state', data.stateJson);
          res({ ok: true });
          break;
        case 'pick-file':
          Mock.pick('.html,.htm', false).then(res);
          break;
        case 'pick-image':
          Mock.pick('image/*', false).then(res);
          break;
        case 'pick-folder':
          Mock.pick(null, true).then(res);
          break;
        case 'to-file-url':
          res({ ok: true, url: 'file:///' + String(data.path).replace(/\\/g, '/') });
          break;
        case 'save-drop':
          res({ ok: false, error: '预览模式不支持保存拖入文件' });
          break;
        case 'compare-upgrade': {
          // 预览模式没有宿主，就拿上次拖入的那份当“当前版本”比一下，至少能把确认流程走通
          var txt = String((data && data.text) || '');
          var pkey = 'pd-src-' + data.appId;
          var prev = localStorage.getItem(pkey);
          localStorage.setItem(pkey, txt);
          var la = String(prev || '').replace(/\r\n?/g, '\n').split('\n');
          var lb = txt.replace(/\r\n?/g, '\n').split('\n');
          var bag = {}, add = 0, rem = 0, fd = -1;
          la.forEach(function (l) { bag[l] = (bag[l] || 0) + 1; });
          lb.forEach(function (l) { if (bag[l] > 0) { bag[l]--; } else add++; });
          for (var bk in bag) if (bag[bk] > 0) rem += bag[bk];
          for (var li = 0; li < Math.min(la.length, lb.length); li++) if (la[li] !== lb[li]) { fd = li + 1; break; }
          if (fd < 0 && la.length !== lb.length) fd = Math.min(la.length, lb.length) + 1;
          var ttl = function (s) {
            var mt = /<title[^>]*>([\s\S]*?)<\/title>/i.exec(s) || /<h1[^>]*>([\s\S]*?)<\/h1>/i.exec(s);
            return mt ? mt[1].replace(/<[^>]*>/g, '').replace(/\s+/g, ' ').trim().slice(0, 40) : '';
          };
          res({ ok: true, cmp: {
            exists: prev != null, same: prev === txt, added: add, removed: rem, firstDiff: fd,
            cur: { title: ttl(prev || ''), size: (prev || '').length, lines: la.length, mtime: null, hash: null, version: null },
            nw: { title: ttl(txt), size: txt.length, lines: lb.length, mtime: null, hash: null, version: null },
            kept: { origin: '（预览）', dir: '', outputFiles: 0, outputBytes: 0 }
          } });
          break;
        }
        case 'apply-upgrade':
          res({ ok: true, backup: null });
          break;
        case 'open-child':
          window.open(data.url, '_blank');
          res({ ok: true });
          break;
        case 'open-tab':
          location.href = data.url;
          res({ ok: true });
          break;
        default:
          res({ ok: false, error: '预览模式不支持 ' + op });
      }
    },
    pick: function (accept, folder) {
      return new Promise(function (res) {
        var i = document.createElement('input');
        i.type = 'file';
        if (folder) { i.webkitdirectory = true; }
        else if (accept) { i.accept = accept; }
        i.onchange = function () {
          if (!i.files || !i.files.length) { res({ ok: false, cancel: true }); return; }
          if (folder) {
            var f0 = i.files[0];
            var parts = (f0.webkitRelativePath || f0.name).split('/');
            var dir = parts[0];
            var entry = null;
            for (var j = 0; j < i.files.length; j++) {
              var rp = i.files[j].webkitRelativePath || '';
              if (rp === dir + '/index.html') { entry = dir + '\\index.html'; break; }
            }
            res({ ok: true, path: dir, entry: entry });
          } else {
            var f = i.files[0];
            res({ ok: true, path: f.name, name: f.name.replace(/\.[^.]+$/, '') });
          }
        };
        i.click();
      });
    }
  };

  /* ---------------- 状态 ---------------- */
  var state = { version: 1, apps: [], settings: {} };
  var DEFAULT_SETTINGS = {
    theme: 'auto',            // dark | light | auto（默认跟随系统）
    closeMode: 'exit',        // exit | tray
    defaultOpenMode: 'inplace',
    favicons: true,
    hotkey: true,             // 全局热键开关
    hotkeyMod: 0x3,           // MOD_ALT(0x1)|MOD_CONTROL(0x2)，与宿主默认一致
    hotkeyVk: 0x44,           // VK_D（'D'）——默认 Ctrl+Alt+D
    topmost: false            // 主窗口置顶（标题栏图钉切换，独立子窗口各自另算）
  };
  var info = { dataDir: '', exeDir: '', version: '0.9.27' };
  var filterTag = '';
  var searchQ = '';          // 搜索词由原生标签栏推送
  var els = {};

  function findApp(id) {
    for (var i = 0; i < state.apps.length; i++) if (state.apps[i].id === id) return state.apps[i];
    return null;
  }
  function uid() { return Math.random().toString(36).slice(2, 10); }
  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }
  function save() {
    call('save-state', { stateJson: JSON.stringify(state) });
  }

  /* ---------------- 主题 ---------------- */
  // 系统的深/浅判定：启动时先用宿主注入的结论（WebView2 首帧问到的 prefers-color-scheme
  // 可能还是上次的旧值，直接问会先闪一帧深色）；之后只在媒体查询真的变化时更新。
  var sysTheme = window.__pdTheme || '';
  function resolveTheme() {
    var t = state.settings.theme || 'auto';
    if (t === 'auto') {
      if (!sysTheme) {
        try { sysTheme = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'; }
        catch (e) { sysTheme = 'dark'; }
      }
      return sysTheme;
    }
    return t === 'light' ? 'light' : 'dark';
  }
  function applyTheme() {
    var r = resolveTheme();
    document.documentElement.setAttribute('data-theme', r);
    // 带上模式：宿主需区分 auto 与固定深浅，auto 时把 profile 的色彩偏好复位为跟随系统
    fire('set-theme', { dark: r === 'dark', mode: state.settings.theme || 'auto' });
  }
  function applySettingsToHost() {
    applyTheme();
    fire('set-close-mode', { mode: state.settings.closeMode || 'exit' });
    var hk = { enabled: state.settings.hotkey !== false };
    // 自定义组合键：仅当用户设过时才下发（旧配置无此字段则沿用宿主默认 Ctrl+Alt+D）
    if (state.settings.hotkeyMod != null) hk.mod = state.settings.hotkeyMod;
    if (state.settings.hotkeyVk != null) hk.vk = state.settings.hotkeyVk;
    fire('set-hotkey', hk);
    fire('set-topmost', { on: state.settings.topmost === true });
  }
  if (window.matchMedia) {
    try {
      window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', function (e) {
        // 只有系统主题真的变了才会触发（profile 摆正后的通知），以它为准更新判定
        sysTheme = e.matches ? 'light' : 'dark';
        if (state.settings.theme === 'auto') applyTheme();
      });
    } catch (e) { }
  }

  /* ---------------- 图标 ---------------- */
  var PALETTE = ['#2DD4BF', '#7AA2F7', '#F59E0B', '#A78BFA', '#34D399', '#F472B6', '#60A5FA', '#FBBF24', '#4ADE80', '#FB923C'];
  function hashColor(s) {
    var h = 0;
    s = String(s || '?');
    for (var i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
    return PALETTE[h % PALETTE.length];
  }
  var iconUrlCache = {};
  function iconUrl(p) {
    if (!p) return null;
    if (iconUrlCache[p]) return iconUrlCache[p];
    if (!hasBridge) { iconUrlCache[p] = 'file:///' + p.replace(/\\/g, '/'); return iconUrlCache[p]; }
    call('to-file-url', { path: p }).then(function (r) {
      if (r && r.url && !iconUrlCache[p]) { iconUrlCache[p] = r.url; render(); }
    });
    return null;
  }
  // 统一解析图标源：data:/http(s):/file: 直接用；否则当本地路径走 iconUrl
  function resolveIconSrc(p) {
    if (!p) return null;
    if (/^(data:|https?:|file:)/i.test(p)) return p;
    return iconUrl(p);
  }

  /* ---------------- 取色面板（原生取色浮层没有“确定”，统一换成带 确定/取消 的面板）----------------
     拦下 input[type=color] 的点击，选色只改面板内的临时值，点确定才把颜色写回原 input 并
     派发 input / change —— 各处原有监听（图标生成、便签字色/高亮/底纹/边框）一律不用改。 */
  var CP_PAL = [
    '#FFFFFF', '#F5F5F5', '#D6DAE0', '#A8AFB8', '#6B7280', '#3A3F47', '#1F2328', '#000000',
    '#EF4444', '#F97316', '#F59E0B', '#FBBF24', '#84CC16', '#22C55E', '#10B981', '#2DD4BF',
    '#06B6D4', '#38BDF8', '#3B82F6', '#6366F1', '#8B5CF6', '#A855F7', '#D946EF', '#EC4899',
    '#FCA5A5', '#FDBA74', '#FDE68A', '#A7F3D0', '#BAE6FD', '#C7D2FE', '#E9D5FF', '#FBCFE8'
  ];
  function cpNorm(v) {
    v = String(v || '').trim().toLowerCase();
    if (v && v.charAt(0) !== '#') v = '#' + v;
    if (/^#[0-9a-f]{3}$/.test(v)) v = '#' + v.charAt(1) + v.charAt(1) + v.charAt(2) + v.charAt(2) + v.charAt(3) + v.charAt(3);
    return /^#[0-9a-f]{6}$/.test(v) ? v : '';
  }
  function openColorPop(anchor, cur, onOk) {
    var old = document.querySelector('.colpop'); if (old) old.remove();
    var sel = cpNorm(cur) || '#000000';
    var p = document.createElement('div'); p.className = 'colpop';
    p.innerHTML =
      '<div class="cp-grid">' + CP_PAL.map(function (c) {
        return '<button type="button" class="cp-sw" data-c="' + c + '" style="background:' + c + '" title="' + c + '"></button>';
      }).join('') + '</div>' +
      '<div class="cp-row"><span class="cp-prev"></span>' +
        '<input type="text" class="cp-hex" maxlength="7" spellcheck="false">' +
        '<button type="button" class="btn ghost cp-more">更多颜色…</button>' +
        '<input type="color" class="cp-native" data-cpn></div>' +
      '<div class="cp-foot"><button type="button" class="btn ghost cp-cancel">取消</button>' +
        '<button type="button" class="btn primary cp-ok">确定</button></div>';
    document.body.appendChild(p);
    var rc = anchor.getBoundingClientRect();
    var pw = p.offsetWidth, ph = p.offsetHeight;
    p.style.left = Math.max(8, Math.min(rc.left, window.innerWidth - pw - 8)) + 'px';
    var top = rc.bottom + 6;   // 放不下就翻到上方，再兜底钳进可视区
    if (top + ph > window.innerHeight - 8) {
      var up = rc.top - ph - 6;
      top = up >= 8 ? up : window.innerHeight - ph - 8;
    }
    p.style.top = Math.max(8, Math.min(top, window.innerHeight - ph - 8)) + 'px';
    function $(n) { return p.querySelector('.' + n); }
    function paint() {
      $('cp-prev').style.background = sel;
      $('cp-hex').value = sel.toUpperCase();
      $('cp-native').value = sel;
      var sw = p.querySelectorAll('.cp-sw');
      for (var i = 0; i < sw.length; i++)
        sw[i].classList.toggle('on', sw[i].getAttribute('data-c').toLowerCase() === sel);
    }
    function set(c) { var v = cpNorm(c); if (v) { sel = v; paint(); } }
    function close() { p.remove(); document.removeEventListener('pointerdown', outside, true); document.removeEventListener('keydown', onKey, true); }
    function ok() { onOk(sel); close(); }
    function outside(e) { if (!p.contains(e.target)) close(); }
    function onKey(e) {
      if (e.key === 'Escape') { e.stopPropagation(); close(); }
      else if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); ok(); }
    }
    p.addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('.cp-sw') : null;
      if (b) set(b.getAttribute('data-c'));
    });
    $('cp-hex').addEventListener('input', function () { set(this.value); });
    $('cp-native').addEventListener('input', function () { set(this.value); });
    $('cp-more').onclick = function () { $('cp-native').click(); };
    $('cp-cancel').onclick = close;
    $('cp-ok').onclick = ok;
    paint();
    setTimeout(function () {
      document.addEventListener('pointerdown', outside, true);
      document.addEventListener('keydown', onKey, true);
    }, 0);
  }
  function installColorPickers(root) {
    (root || document).addEventListener('click', function (e) {
      var t = e.target;
      if (!t || t.tagName !== 'INPUT' || t.type !== 'color' || t.hasAttribute('data-cpn')) return;
      e.preventDefault(); e.stopPropagation();
      openColorPop(t, t.value, function (hex) {
        t.value = hex;
        t.dispatchEvent(new Event('input', { bubbles: true }));
        t.dispatchEvent(new Event('change', { bubbles: true }));
      });
    }, true);
  }

  /* ---------------- 内置程序化图标生成（离线、即时，产出 PNG data URL）---------------- */
  function shade(hex, amt) { // amt -1..1
    hex = (hex || '#000000').replace('#', '');
    if (hex.length === 3) hex = hex.split('').map(function (x) { return x + x; }).join('');
    var r = parseInt(hex.substr(0, 2), 16), g = parseInt(hex.substr(2, 2), 16), b = parseInt(hex.substr(4, 2), 16);
    function c(v) { return amt >= 0 ? Math.round(v + (255 - v) * amt) : Math.round(v * (1 + amt)); }
    return '#' + [c(r), c(g), c(b)].map(function (v) { return ('0' + v.toString(16)).slice(-2); }).join('');
  }
  function roundRectPath(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }
  // 形状：圆角方（半径可调）/ 圆 / 六边形 / 菱形 / 徽章盾形
  function iconPath(ctx, S, shape, rad) {
    if (shape === 'circle') { ctx.beginPath(); ctx.arc(S / 2, S / 2, S / 2 - 2, 0, Math.PI * 2); return; }
    if (shape === 'hex') {
      ctx.beginPath();
      for (var i = 0; i < 6; i++) {
        var a = Math.PI / 180 * (60 * i - 90);
        var x = S / 2 + (S / 2 - 2) * Math.cos(a), y = S / 2 + (S / 2 - 2) * Math.sin(a);
        if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y);
      }
      ctx.closePath(); return;
    }
    if (shape === 'diamond') {
      ctx.beginPath(); ctx.moveTo(S / 2, 2); ctx.lineTo(S - 2, S / 2);
      ctx.lineTo(S / 2, S - 2); ctx.lineTo(2, S / 2); ctx.closePath(); return;
    }
    if (shape === 'shield') {
      var m = 2, w = S - 4, r = Math.max(10, rad * 0.6);
      ctx.beginPath();
      ctx.moveTo(m + r, m);
      ctx.lineTo(S - m - r, m);
      ctx.quadraticCurveTo(S - m, m, S - m, m + r);
      ctx.lineTo(S - m, m + w * 0.56);
      ctx.quadraticCurveTo(S - m, m + w * 0.82, S / 2, S - m);
      ctx.quadraticCurveTo(m, m + w * 0.82, m, m + w * 0.56);
      ctx.lineTo(m, m + r);
      ctx.quadraticCurveTo(m, m, m + r, m);
      ctx.closePath(); return;
    }
    roundRectPath(ctx, 2, 2, S - 4, S - 4, Math.max(0, Math.min(rad, S / 2 - 2)));
  }
  // 底色：淡色底（与内置字母块同款）/ 纯色 / 斜向渐变 / 径向高光 / 对角双色
  function hexA(hex, a) {
    var v = cpNorm(hex) || '#000000';
    return 'rgba(' + parseInt(v.substr(1, 2), 16) + ',' + parseInt(v.substr(3, 2), 16) + ',' + parseInt(v.substr(5, 2), 16) + ',' + a + ')';
  }
  function iconBG(ctx, S, o) {
    var g;
    if (o.grad === 'soft') {   // 首页默认字母块就是 12% 透明度的主题色，这里画得一模一样
      ctx.fillStyle = hexA(o.bg, 0.13); ctx.fillRect(0, 0, S, S);
      g = ctx.createLinearGradient(0, 0, S, S);
      g.addColorStop(0, hexA(o.bg, 0.07)); g.addColorStop(1, 'rgba(0,0,0,0)');
      ctx.fillStyle = g; ctx.fillRect(0, 0, S, S);
      return;
    }
    if (o.grad === 'diag') {
      ctx.fillStyle = o.bg; ctx.fillRect(0, 0, S, S);
      ctx.fillStyle = o.bg2 || shade(o.bg, -0.22);
      ctx.beginPath(); ctx.moveTo(S, 0); ctx.lineTo(S, S); ctx.lineTo(0, S); ctx.closePath(); ctx.fill();
      return;
    }
    if (o.grad === 'radial') {
      g = ctx.createRadialGradient(S * 0.35, S * 0.3, S * 0.04, S * 0.5, S * 0.52, S * 0.8);
      g.addColorStop(0, shade(o.bg, 0.3)); g.addColorStop(1, shade(o.bg, -0.2));
    } else if (o.grad === 'linear') {
      g = ctx.createLinearGradient(0, 0, S, S);
      g.addColorStop(0, shade(o.bg, 0.18)); g.addColorStop(1, shade(o.bg, -0.16));
    } else { ctx.fillStyle = o.bg; ctx.fillRect(0, 0, S, S); return; }
    ctx.fillStyle = g; ctx.fillRect(0, 0, S, S);
  }
  function iconTex(ctx, S, kind) {
    if (!kind || kind === 'none') return;
    ctx.save();
    ctx.globalAlpha = 0.13;
    ctx.strokeStyle = ctx.fillStyle = '#FFFFFF';
    var i;
    if (kind === 'dots') {
      for (var y = S * 0.12; y < S; y += S * 0.14)
        for (var x = S * 0.12; x < S; x += S * 0.14) { ctx.beginPath(); ctx.arc(x, y, S * 0.014, 0, Math.PI * 2); ctx.fill(); }
    } else if (kind === 'stripe') {
      ctx.lineWidth = S * 0.035;
      for (var d = -S; d < S * 2; d += S * 0.16) { ctx.beginPath(); ctx.moveTo(d, 0); ctx.lineTo(d + S, S); ctx.stroke(); }
    } else {
      ctx.lineWidth = Math.max(1, S * 0.012);
      for (i = S * 0.1; i < S; i += S * 0.16) { ctx.beginPath(); ctx.moveTo(i, 0); ctx.lineTo(i, S); ctx.moveTo(0, i); ctx.lineTo(S, i); ctx.stroke(); }
    }
    ctx.restore();
  }
  // 文字组合：1–3 字单行、4 字自动上下双行；字号按最长行自适应并回缩到画布内
  function iconLines(t, upper) {
    t = String(t || '').replace(/\s+/g, ' ').trim();
    if (upper) t = t.toUpperCase();
    t = t.slice(0, 4);
    if (t.length === 4) return [t.slice(0, 2), t.slice(2)];
    return t ? [t] : [];
  }
  var IG_FONTS = {
    '雅黑': '"Microsoft YaHei UI","Microsoft YaHei",system-ui,sans-serif',
    '等线': '"DengXian","Microsoft YaHei UI",sans-serif',
    '黑体': '"SimHei","Microsoft YaHei UI",sans-serif',
    '宋体': '"SimSun","Songti SC",serif',
    '无衬线': 'system-ui,Arial,sans-serif',
    '等宽': '"Consolas","Courier New",monospace'
  };
  function genIconURL(o) {
    var S = 128, cv = document.createElement('canvas'); cv.width = S; cv.height = S;
    var ctx = cv.getContext('2d'); if (!ctx) return null;
    ctx.clearRect(0, 0, S, S);
    iconPath(ctx, S, o.shape || 'round', o.radius == null ? 32 : o.radius);
    ctx.save(); ctx.clip();
    iconBG(ctx, S, o);
    iconTex(ctx, S, o.tex);
    if (o.gloss && o.grad !== 'soft') {  // 淡色底上再加柔光就糊成一片白，等于把颜色调没了
      var gg = ctx.createLinearGradient(0, 0, 0, S * 0.6);
      gg.addColorStop(0, 'rgba(255,255,255,.26)'); gg.addColorStop(1, 'rgba(255,255,255,0)');
      ctx.fillStyle = gg; ctx.fillRect(0, 0, S, S * 0.6);
    }
    ctx.restore();
    var lines = iconLines(o.text, o.upper !== false);
    if (lines.length) {
      var maxc = 0, i;
      for (i = 0; i < lines.length; i++) maxc = Math.max(maxc, lines[i].length);
      var fs = Math.round(S * (maxc === 1 ? 0.66 : maxc === 2 ? 0.46 : maxc === 3 ? 0.34 : 0.3));
      var fam = IG_FONTS[o.font] || IG_FONTS['雅黑'];
      ctx.fillStyle = o.fg || '#FFFFFF';
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.font = '700 ' + fs + 'px ' + fam;
      // 各形状的内接空间差很多：菱形、盾形、六边形往中间收，否则文字会贴着被裁掉的边
      var sh = o.shape || 'round';
      var lim = S * (sh === 'diamond' ? 0.6 : (sh === 'hex' || sh === 'shield') ? 0.72 : sh === 'circle' ? 0.76 : 0.84);
      var wmax = 0;
      for (i = 0; i < lines.length; i++) wmax = Math.max(wmax, ctx.measureText(lines[i]).width);
      if (wmax > lim) { fs = Math.max(10, Math.floor(fs * lim / wmax)); ctx.font = '700 ' + fs + 'px ' + fam; }
      var lh = fs * (lines.length > 1 ? 0.98 : 1);
      // 盾形下沿收尖、菱形上下都是尖角，文字得往上挪一点才看着居中
      var y0 = S / 2 - (lines.length - 1) * lh / 2 + S * (sh === 'shield' ? -0.04 : sh === 'diamond' ? -0.01 : 0.01);
      for (i = 0; i < lines.length; i++) {
        var y = y0 + i * lh;
        if (o.stroke) {
          ctx.lineWidth = Math.max(2, fs * 0.07); ctx.lineJoin = 'round';
          ctx.strokeStyle = 'rgba(0,0,0,.3)'; ctx.strokeText(lines[i], S / 2, y);
        }
        ctx.fillText(lines[i], S / 2, y);
      }
    }
    try { return cv.toDataURL('image/png'); } catch (e) { return null; }
  }
  function openIconGen(anchor, init, onPick) {
    var old = document.querySelector('.icongen'); if (old) old.remove();
    var p = document.createElement('div'); p.className = 'export-menu icongen';
    p.style.cssText += 'width:276px;padding:10px;gap:8px;max-height:calc(100vh - 16px);overflow:auto';
    var INP = 'height:24px;background:var(--bg);border:1px solid var(--border);border-radius:6px;color:var(--text);outline:none;font-family:inherit;padding:0 5px';
    var CLR = 'width:26px;height:22px;border:1px solid var(--border);border-radius:5px;background:transparent;padding:0;cursor:pointer';
    var LB = 'display:flex;align-items:center;gap:5px;font-size:12px;color:var(--text-2)';
    function opt(list, cur) {
      return list.map(function (v) {
        return '<option value="' + v[0] + '"' + (v[0] === cur ? ' selected' : '') + '>' + v[1] + '</option>';
      }).join('');
    }
    p.innerHTML =
      '<div style="display:flex;align-items:center;gap:10px">' +
        '<canvas id="ig-pv" width="128" height="128" style="width:56px;height:56px;flex:none"></canvas>' +
        '<div style="flex:1;display:flex;flex-direction:column;gap:6px">' +
          '<label style="' + LB + '">文字<input type="text" id="ig-text" maxlength="4" style="' + INP + ';width:52px"></label>' +
          '<label style="' + LB + '">形状<select id="ig-shape" style="' + INP + ';flex:1">' +
            opt([['round', '圆角方'], ['circle', '圆形'], ['hex', '六边形'], ['diamond', '菱形'], ['shield', '徽章盾']], 'round') + '</select></label>' +
        '</div>' +
      '</div>' +
      '<div style="display:flex;align-items:center;gap:7px;font-size:12px;color:var(--text-2)">' +
        '底色<input type="color" id="ig-bg" value="#2DD4BF" style="' + CLR + '">' +
        '副色<input type="color" id="ig-bg2" value="#0F766E" style="' + CLR + '" title="仅「对角双色」生效">' +
        '字色<input type="color" id="ig-fg" value="#FFFFFF" style="' + CLR + '">' +
      '</div>' +
      '<div style="display:flex;align-items:center;gap:6px">' +
        '<select id="ig-grad" style="' + INP + ';flex:1">' +
          opt([['soft', '淡色底（默认）'], ['linear', '斜向渐变'], ['radial', '径向高光'], ['diag', '对角双色'], ['none', '纯色']], 'soft') + '</select>' +
        '<select id="ig-tex" style="' + INP + ';flex:1">' +
          opt([['none', '无纹理'], ['dots', '点阵'], ['stripe', '斜纹'], ['grid', '网格']], 'none') + '</select>' +
      '</div>' +
      '<div style="display:flex;align-items:center;gap:6px">' +
        '<select id="ig-font" style="' + INP + ';flex:1">' +
          opt([['雅黑', '雅黑 粗'], ['等线', '等线'], ['黑体', '黑体'], ['宋体', '宋体'], ['无衬线', '系统无衬线'], ['等宽', '等宽']], '雅黑') + '</select>' +
        '<label style="' + LB + ';flex:1">圆角<input type="range" id="ig-rad" min="0" max="56" value="32" style="flex:1;height:22px;min-width:40px"></label>' +
      '</div>' +
      '<div style="display:flex;align-items:center;gap:10px;font-size:12px;color:var(--text-2);flex-wrap:wrap">' +
        '<label style="display:flex;align-items:center;gap:4px"><input type="checkbox" id="ig-upper" checked>大写</label>' +
        '<label style="display:flex;align-items:center;gap:4px"><input type="checkbox" id="ig-gloss" checked>柔光</label>' +
        '<label style="display:flex;align-items:center;gap:4px"><input type="checkbox" id="ig-stroke">描边</label>' +
      '</div>' +
      '<div style="display:flex;gap:6px;justify-content:flex-end">' +
        '<button class="btn ghost" id="ig-rand" type="button">随机配色</button>' +
        '<button class="btn" id="ig-use" type="button">使用此图标</button></div>';
    document.body.appendChild(p);
    var rc = anchor.getBoundingClientRect();
    var pw = p.offsetWidth, ph = p.offsetHeight;
    p.style.left = Math.max(8, Math.min(rc.left, window.innerWidth - pw - 8)) + 'px';
    // 默认窗口不够高时往上翻，再不行顶到可视区内，避免面板被窗口裁掉
    var top = rc.bottom + 6;
    if (top + ph > window.innerHeight - 8) {
      var up = rc.top - ph - 6;
      top = up >= 8 ? up : window.innerHeight - ph - 8;
    }
    p.style.top = Math.max(8, Math.min(top, window.innerHeight - ph - 8)) + 'px';
    var $ = function (id) { return p.querySelector('#' + id); };
    function current() {
      return {
        text: $('ig-text').value, bg: $('ig-bg').value, bg2: $('ig-bg2').value, fg: $('ig-fg').value,
        shape: $('ig-shape').value, radius: parseInt($('ig-rad').value, 10) || 0,
        grad: $('ig-grad').value, tex: $('ig-tex').value, font: $('ig-font').value,
        upper: $('ig-upper').checked, gloss: $('ig-gloss').checked, stroke: $('ig-stroke').checked
      };
    }
    // 圆角只对圆角方有意义，副色只对「对角双色」有意义——但控件不能点不动：
    // 副色淡出表示"当前不生效"仍可点，一旦选了副色就自动切到对角双色，改动立刻可见
    var fgTouched = false;
    function syncFg() {
      if (fgTouched) return;   // 用户手动定过字色，就不再自动跟
      $('ig-fg').value = $('ig-grad').value === 'soft' ? $('ig-bg').value : '#FFFFFF';
    }
    function syncAid() {
      $('ig-rad').disabled = $('ig-shape').value !== 'round';
      $('ig-bg2').style.opacity = $('ig-grad').value === 'diag' ? '1' : '0.45';
      $('ig-upper').disabled = /[\u4e00-\u9fa5]/.test($('ig-text').value);  // 中文没有大小写
    }
    function preview() { var u = genIconURL(current()); var c = $('ig-pv').getContext('2d'); c.clearRect(0, 0, 128, 128); if (u) { var im = new Image(); im.onload = function () { c.clearRect(0, 0, 128, 128); c.drawImage(im, 0, 0, 128, 128); }; im.src = u; } }
    var IGIDS = ['ig-text', 'ig-bg', 'ig-bg2', 'ig-fg', 'ig-shape', 'ig-rad', 'ig-grad', 'ig-tex', 'ig-font', 'ig-upper', 'ig-gloss', 'ig-stroke'];
    IGIDS.forEach(function (id) {
      $(id).addEventListener('input', function () {
        if (id === 'ig-fg') fgTouched = true;
        if (id === 'ig-bg2' && $('ig-grad').value !== 'diag') $('ig-grad').value = 'diag';
        syncFg(); syncAid(); preview();
      });
      $(id).addEventListener('change', function () { syncFg(); syncAid(); preview(); });
    });
    $('ig-rand').onclick = function () {
      var c = PALETTE[Math.floor(Math.random() * PALETTE.length)];
      var SH = ['round', 'circle', 'hex', 'diamond', 'shield'], GR = ['soft', 'linear', 'radial', 'diag', 'none'], TX = ['none', 'dots', 'stripe', 'grid'];
      $('ig-bg').value = c;
      $('ig-bg2').value = shade(c, -0.3);
      $('ig-shape').value = SH[Math.floor(Math.random() * SH.length)];
      $('ig-rad').value = 8 + Math.floor(Math.random() * 48);
      $('ig-grad').value = GR[Math.floor(Math.random() * GR.length)];
      $('ig-tex').value = TX[Math.floor(Math.random() * TX.length)];
      syncFg(); syncAid(); preview();
    };
    function done() { p.remove(); document.removeEventListener('pointerdown', outside, true); }
    function outside(e) {
      if (p.contains(e.target)) return;
      var cp = document.querySelector('.colpop');   // 取色面板挂在 body 上，点它不算离开生成器
      if (cp && cp.contains(e.target)) return;
      done();
    }
    setTimeout(function () { document.addEventListener('pointerdown', outside, true); }, 0);
    $('ig-use').onclick = function () { var u = genIconURL(current()); if (u) onPick(u); done(); };
    $('ig-text').value = (init && init.text) || 'A';
    $('ig-bg').value = (init && init.bg) || hashColor((init && init.text) || 'A');
    $('ig-bg2').value = shade($('ig-bg').value, -0.3);
    syncFg();
    syncAid();
    preview();
  }
  // favicon 兜底链：站点 origin/favicon.ico → Google s2 → 字母块
  window.favFallback = function (img) {
    var t = img.parentNode;
    var step = t.getAttribute('data-step') || '1';
    if (step === '1') {
      t.setAttribute('data-step', '2');
      img.src = 'https://www.google.com/s2/favicons?domain=' + (t.getAttribute('data-host') || '') + '&sz=64';
      return;
    }
    img.remove();
  };
  // 自定义图标文件读不出来（换机、被移走）：退回字母块，不留空格子
  window.iconFail = function (img) {
    var t = img.parentNode;
    var c = t.getAttribute('data-fb') || '#888';
    t.classList.remove('img');
    t.innerHTML = '<span class="ltr" style="background:' + c + '1f;color:' + c + '">' +
      (t.getAttribute('data-fl') || '•') + '</span>';
  };
  function tileHtml(a) {
    var c = hashColor(a.name);
    var letter = '<span class="ltr" style="background:' + c + '1f;color:' + c + '">' +
      esc((a.name || '?').trim().charAt(0).toUpperCase() || '•') + '</span>';
    if (a.icon) {
      var u = resolveIconSrc(a.icon);
      // 自定义图标：只画图标本身。以前还垫一层字母底色，图标外圈就多出一圈淡色方块
      if (u) return '<div class="tile img" data-fb="' + esc(c) + '" data-fl="' +
        esc((a.name || '?').trim().charAt(0).toUpperCase() || '•') + '">' +
        '<img src="' + esc(u) + '" onerror="iconFail(this)"></div>';
      return '<div class="tile">' + letter + '</div>';   // 路径还没解析成 URL，先占位
    }
    if (a.kind === 'url' && state.settings.favicons !== false) {
      var origin = '', host = '';
      try { var u2 = new URL(a.target); origin = u2.origin; host = u2.hostname; } catch (e) { }
      if (origin) {
        return '<div class="tile img" data-host="' + esc(host) + '" data-fb="' + esc(c) + '" data-fl="' +
          esc((a.name || '?').trim().charAt(0).toUpperCase() || '•') + '">' + letter +
          '<img src="' + esc(origin) + '/favicon.ico" onerror="favFallback(this)"></div>';
      }
    }
    return '<div class="tile">' + letter + '</div>';
  }
  function kindLabel(k) { return k === 'url' ? '网址' : (k === 'folder' ? '文件夹' : '文件'); }

  function shortTarget(a) {
    if (a.kind === 'url') {
      try {
        var u = new URL(a.target);
        return u.host + (u.pathname && u.pathname !== '/' ? u.pathname : '');
      } catch (e) { return a.target || ''; }
    }
    if (a.kind === 'file') return String(a.target).split('\\').pop();
    if (a.kind === 'folder') {
      var dir = String(a.target).split('\\').pop();
      var entry = a.entry ? String(a.entry).split('\\').pop() : 'index.html';
      return entry + ' · ' + dir;
    }
    return a.target || '';
  }

  /* ---------------- 渲染 ---------------- */
  function renderChips() {
    var tags = {};
    state.apps.forEach(function (a) {
      (a.tags || []).forEach(function (t) { tags[t] = (tags[t] || 0) + 1; });
    });
    var keys = Object.keys(tags).sort();
    if (!keys.length) { els.chips.classList.add('hidden'); els.chips.innerHTML = ''; return; }
    els.chips.classList.remove('hidden');
    var html = '<button class="chip-tag' + (filterTag === '' ? ' on' : '') + '" data-t="">全部 <b>' + state.apps.length + '</b></button>';
    keys.forEach(function (t) {
      html += '<button class="chip-tag' + (filterTag === t ? ' on' : '') + '" data-t="' + esc(t) + '">' +
        esc(t) + ' <b>' + tags[t] + '</b></button>';
    });
    els.chips.innerHTML = html;
  }

  /* 布局动画（FLIP）：
     以前每次 render 都重建 innerHTML，节点全新一遍，位置变化只能“瞬移”，
     拖窗口改宽度时整片图标就是一跳一跳的。现在：
     1) 节点按 id 复用，位置变化用 transform 从旧位置补间到新位置；
     2) 连续变宽时列数不变 → 交给 CSS 1fr 自己平滑收缩，不插动画；
     3) 列数变了 → 才跑一次 FLIP，让换行变成滑过去而不是弹过去。 */
  var nodes = {};       // key -> 元素
  var posCache = {};    // key -> [offsetLeft, offsetTop]
  var lastCols = 0;

  function noMotion() {
    try { return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches; }
    catch (e) { return false; }
  }

  function measure() {
    var m = {};
    var kids = els.grid.children;
    for (var i = 0; i < kids.length; i++) {
      var el = kids[i];
      if (!el.dataset || !el.dataset.key) continue;
      m[el.dataset.key] = [el.offsetLeft, el.offsetTop];   // offset* 不受 transform 影响，免去刷 transform
    }
    return m;
  }

  function flipFrom(prev) {
    var cur = measure();
    if (noMotion()) return cur;
    var moved = [];
    for (var k in cur) {
      var el = nodes[k];
      if (!el) continue;
      var p = prev[k];
      if (!p) {
        if (k !== '__add') {
          el.classList.add('pop-in');
          (function (e2) { setTimeout(function () { e2.classList.remove('pop-in'); }, 340); })(el);
        }
        continue;
      }
      var dx = p[0] - cur[k][0], dy = p[1] - cur[k][1];
      if (Math.abs(dx) < 1 && Math.abs(dy) < 1) continue;
      el.style.transition = 'none';
      el.style.transform = 'translate(' + dx + 'px,' + dy + 'px)';
      moved.push(el);
    }
    if (!moved.length) return cur;
    // 必须强制一次布局把“起点”交给渲染器，否则下面同一次任务里就把 transform 改了回去，
    // 浏览器只会看到首尾一致，过渡根本不跑（rAF 也来不及：它仍在本次绘制之前）。
    void els.grid.offsetWidth;
    moved.forEach(function (el) {
      el.style.transition = 'transform .26s cubic-bezier(.22,.9,.28,1)';
      el.style.transform = '';
    });
    setTimeout(function () {
      moved.forEach(function (el) { el.style.transition = ''; });
    }, 320);
    return cur;
  }

  function colCount() {
    try {
      var t = getComputedStyle(els.grid).gridTemplateColumns;
      return t ? t.split(' ').filter(Boolean).length : 0;
    } catch (e) { return 0; }
  }

  var resizeRaf = 0;
  function onViewportResize() {
    if (resizeRaf) return;
    resizeRaf = requestAnimationFrame(function () {
      resizeRaf = 0;
      hideTip();
      closeFly(true);
      var c = colCount();
      if (lastCols && c !== lastCols) posCache = flipFrom(posCache);
      else posCache = measure();
      lastCols = c;
    });
  }

  function bySort(x, y) {
    var pa = x.pinned ? 1 : 0, pb = y.pinned ? 1 : 0;
    if (pa !== pb) return pb - pa;
    return (y.lastOpenedAt || 0) - (x.lastOpenedAt || 0) ||
           (y.createdAt || 0) - (x.createdAt || 0);
  }

  /* 手动顺序（v0.8.2）：网格与浮层内都按持久化的 ord 排列，由用户拖拽决定，
     不再按“最近打开”自动上浮。首次加载用旧的自动排序补齐 ord，升级不打乱现有观感。 */
  function byOrd(x, y) {
    return (x.ord || 0) - (y.ord || 0) || (x.createdAt || 0) - (y.createdAt || 0);
  }
  function idxOf(arr, id) { for (var i = 0; i < arr.length; i++) if (arr[i].id === id) return i; return -1; }
  function topSorted() { return state.apps.filter(function (a) { return !a.g; }).sort(byOrd); }
  function nextTopOrd() {
    var m = 0;
    state.apps.forEach(function (a) { if (!a.g && (a.ord || 0) > m) m = a.ord || 0; });
    return m + 1;
  }
  function normalizeOrd() {
    var hasNull = state.apps.some(function (a) { return a.ord == null; });
    if (!hasNull) return;
    state.apps.filter(function (a) { return !a.g; }).sort(bySort)
      .forEach(function (a, i) { a.ord = i + 1; });
    var byg = {};
    state.apps.forEach(function (a) { if (a.g) { (byg[a.g] = byg[a.g] || []).push(a); } });
    Object.keys(byg).forEach(function (gid) {
      byg[gid].sort(bySort).forEach(function (a, i) { a.ord = i + 1; });
    });
  }
  /* 在同一列表内重排：src 移到 refId 的前(before)或后。顶层之间、或同一组的成员之间。 */
  function reorderWithin(src, refId, before) {
    var arr = src.g ? membersOf(src.g) : topSorted();
    var si = idxOf(arr, src.id), ri = idxOf(arr, refId);
    if (si < 0 || ri < 0 || src.id === refId) return false;
    arr.splice(si, 1);
    ri = idxOf(arr, refId);
    arr.splice(before ? ri : ri + 1, 0, src);
    arr.forEach(function (a, i) { a.ord = i + 1; });
    return true;
  }
  function appendToEnd(src) {
    var arr = topSorted();
    var si = idxOf(arr, src.id);
    if (si >= 0) arr.splice(si, 1);
    arr.push(src);
    arr.forEach(function (a, i) { a.ord = i + 1; });
  }

  function render() {
    if (!els.grid) return;
    renderChips();
    hideTip();
    closeFly(true);
    var prev = posCache;
    var q = searchQ.trim().toLowerCase();
    // 搜索/按标签筛选时把分组“打平”，成员直接当普通磁贴展示，否则藏在组里的应搜不到
    var flat = !!q || !!filterTag;
    var list;
    if (flat) {
      list = state.apps.filter(function (a) {
        if (isGroup(a)) return false;
        if (filterTag && (a.tags || []).indexOf(filterTag) < 0) return false;
        if (!q) return true;
        return (a.name + ' ' + (a.target || '') + ' ' + (a.tags || []).join(' ')).toLowerCase().indexOf(q) >= 0;
      });
    } else {
      list = state.apps.filter(function (a) { return !a.g; });
    }
    list.sort(byOrd);

    var keep = {};
    list.forEach(function (a) {
      var el = nodes[a.id];
      if (!el) { el = card(a); nodes[a.id] = el; }
      else updateCard(el, a);
      keep[a.id] = 1;
      els.grid.appendChild(el);          // 已存在的节点：appendChild 等于调顺序
    });
    Object.keys(nodes).forEach(function (k) {
      if (keep[k] || k === '__add') return;
      nodes[k].remove();
      delete nodes[k];
    });
    if (!flat && state.apps.length) {
      if (!nodes.__add) { nodes.__add = ghostCard(); }
      keep.__add = 1;
      els.grid.appendChild(nodes.__add);
    } else if (nodes.__add) {
      nodes.__add.remove();
      delete nodes.__add;
    }

    var nGroup = 0, nApp = 0;
    state.apps.forEach(function (a) { if (isGroup(a)) nGroup++; else nApp++; });
    els.empty.classList.toggle('hidden', state.apps.length > 0);
    els.grid.classList.toggle('hidden', state.apps.length === 0);
    els.statLeft.textContent = nApp + ' 个应用' +
      (nGroup ? ' · ' + nGroup + ' 个分组' : '') +
      (filterTag ? ' · 标签「' + filterTag + '」' : '');
    posCache = flipFrom(prev);
    lastCols = colCount();
  }

  /* 卡片：单层图标磁贴（名称/来源不占版面，悬停 2s 由 cardtip 展示；右键更多操作） */
  function card(a) {
    var el = document.createElement('article');
    el.dataset.key = a.id;
    el.dataset.id = a.id;
    el.tabIndex = 0;
    bindCard(el);
    updateCard(el, a);
    return el;
  }

  function updateCard(el, a) {
    el.className = 'card' + (isGroup(a) ? ' grp' : '') + (a.pinned ? ' pinned' : '');
    el.setAttribute('aria-label', a.name);
    el.innerHTML = isGroup(a) ? folderHtml(a) : tileHtml(a);
  }

  /* 事件只绑一次，响应时按 id 回查数据，节点复用才不会拿着旧对象 */
  function bindCard(el) {
    el.addEventListener('mouseenter', function () {
      var a = findApp(el.dataset.id);
      if (!a) return;
      if (isGroup(a)) hoverGroup(el, a); else showTipFor(el, a);
    });
    el.addEventListener('mouseleave', function () {
      hideTip();
      if (!fly.pinned) scheduleCloseFly();
    });
    el.addEventListener('click', function () {
      if (justDragged) return;
      var a = findApp(el.dataset.id);
      if (!a) return;
      if (isGroup(a)) toggleGroupOpen(el, a); else openApp(a);
    });
    el.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter') return;
      var a = findApp(el.dataset.id);
      if (!a) return;
      if (isGroup(a)) toggleGroupOpen(el, a); else openApp(a);
    });
    el.addEventListener('contextmenu', function (e) {
      var a = findApp(el.dataset.id);
      if (!a) return;
      e.preventDefault();
      hideTip();
      openCtx(e.clientX, e.clientY, a);
    });
    el.addEventListener('pointerdown', function (e) { onItemPointerDown(e, el); });
  }

  /* ---------------- 右键菜单 ---------------- */
  var ctxEl = null;
  var pinned = {};   // 已钉到桌面的应用（会话内）
  function closeCtx() {
    if (!ctxEl) return;
    ctxEl.remove();
    ctxEl = null;
    document.removeEventListener('pointerdown', onDocDown, true);
    document.removeEventListener('scroll', closeCtx, true);
    window.removeEventListener('blur', closeCtx);
    window.removeEventListener('keydown', onKeyCloseCtx, true);
  }
  function onDocDown(e) { if (ctxEl && !ctxEl.contains(e.target)) closeCtx(); }
  function onKeyCloseCtx(e) { if (e.key === 'Escape') closeCtx(); }
  function openCtx(x, y, a) {
    closeCtx();
    if (isGroup(a)) { openGroupCtx(x, y, a); return; }
    var isPinned = !!pinned[a.id];
    var m = document.createElement('div');
    m.className = 'ctx';
    m.innerHTML =
      '<button data-act="window">独立窗口打开</button>' +
      '<button data-act="widget">' + (isPinned ? '从桌面取消钉住' : '钉到桌面') + '</button>' +
      '<button data-act="pin">' + (a.pinned ? '取消置顶' : '置顶显示') + '</button>' +
      (a.g ? '<button data-act="leave">从分组中移出</button>'
           : '<button data-act="togroup">加入分组…</button>') +
      (a.kind === 'file' || a.kind === 'folder'
        ? '<button data-act="upgrade">升级到新版本…</button>' : '') +
      '<button data-act="edit">编辑…</button>' +
      '<button data-act="del" class="danger">删除…</button>';
    m.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (!b) return;
      closeCtx();
      if (b.dataset.act === 'window') openWindow(a);
      else if (b.dataset.act === 'upgrade') upgradeFromPick(a);
      else if (b.dataset.act === 'togroup') pickGroupDialog(a);
      else if (b.dataset.act === 'leave') leaveGroup(a);
      else if (b.dataset.act === 'widget') {
        if (isPinned) {
          delete pinned[a.id];
          a.wp = false; save();
          fire('close-widget', { appId: a.id });
        } else {
          pinned[a.id] = true;
          resolveUrl(a).then(function (url) {
            if (!url) { delete pinned[a.id]; return; }
            a.wp = true; save();
            call('open-widget', {
              url: url, title: a.name, appId: a.id,
              x: a.wx || 0, y: a.wy || 0,
              width: a.ww || 380, height: a.wh || 280
            });
            toast('已钉到桌面');
          });
        }
      }
      else if (b.dataset.act === 'pin') {
        a.pinned = !a.pinned;
        save(); render();
        toast(a.pinned ? '已置顶「' + a.name + '」' : '已取消置顶');
      }
      else if (b.dataset.act === 'edit') editDialog(a);
      else if (b.dataset.act === 'del') confirmDelete(a);
    });
    document.body.appendChild(m);
    m.style.left = Math.max(8, Math.min(x, window.innerWidth - m.offsetWidth - 8)) + 'px';
    m.style.top = Math.max(8, Math.min(y, window.innerHeight - m.offsetHeight - 8)) + 'px';
    ctxEl = m;
    document.addEventListener('pointerdown', onDocDown, true);
    document.addEventListener('scroll', closeCtx, true);
    window.addEventListener('blur', closeCtx);
    window.addEventListener('keydown', onKeyCloseCtx, true);
  }

  /* 分组的右键菜单 */
  function openGroupCtx(x, y, g) {
    var n = membersOf(g).length;
    var m = document.createElement('div');
    m.className = 'ctx';
    m.innerHTML =
      '<button data-act="all">打开全部（' + n + '）</button>' +
      '<button data-act="rename">重命名…</button>' +
      '<button data-act="pin">' + (g.pinned ? '取消置顶' : '置顶显示') + '</button>' +
      '<button data-act="ungroup">取消分组</button>' +
      '<button data-act="del" class="danger">删除分组与应用…</button>';
    m.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (!b) return;
      closeCtx();
      if (b.dataset.act === 'all') openAll(g);
      else if (b.dataset.act === 'rename') renameGroup(g);
      else if (b.dataset.act === 'pin') { g.pinned = !g.pinned; save(); render(); }
      else if (b.dataset.act === 'ungroup') ungroup(g);
      else if (b.dataset.act === 'del') confirmDelete(g);
    });
    document.body.appendChild(m);
    m.style.left = Math.max(8, Math.min(x, window.innerWidth - m.offsetWidth - 8)) + 'px';
    m.style.top = Math.max(8, Math.min(y, window.innerHeight - m.offsetHeight - 8)) + 'px';
    ctxEl = m;
    document.addEventListener('pointerdown', onDocDown, true);
    document.addEventListener('scroll', closeCtx, true);
    window.addEventListener('blur', closeCtx);
    window.addEventListener('keydown', onKeyCloseCtx, true);
  }

  function ghostCard() {
    var b = document.createElement('button');
    b.className = 'card ghost-add';
    b.dataset.key = '__add';      // 有 key 才能进 measure，但不给 id：拖拽时它不是有效目标
    b.title = '添加应用（网址 / HTML 文件 / 文件夹）';
    b.innerHTML = '<div class="tile">＋</div>';
    b.addEventListener('click', addDialog);
    return b;
  }

  /* ---------------- 悬停 2s 信息提示 ---------------- */
  var tipTimer = null;
  function hideTip() {
    if (tipTimer) { clearTimeout(tipTimer); tipTimer = null; }
    if (els.cardtip) els.cardtip.classList.remove('open');
  }
  function showTipFor(el, a) {
    hideTip();
    tipTimer = setTimeout(function () {
      var tip = els.cardtip;
      if (!tip) return;
      tip.innerHTML =
        '<b>' + esc(a.name) + (a.pinned ? ' · 置顶' : '') + '</b>' +
        '<span class="trow">类型：' + kindLabel(a.kind) + '</span>' +
        '<span class="trow">' + esc(shortTarget(a)) + '</span>' +
        ((a.tags && a.tags.length) ? '<span class="trow">标签：' + esc(a.tags.join('、')) + '</span>' : '');
      tip.classList.add('open');
      var r = el.getBoundingClientRect();
      var tw = tip.offsetWidth, th = tip.offsetHeight;
      var x = Math.min(Math.max(8, r.left + r.width / 2 - tw / 2), window.innerWidth - tw - 8);
      var y = r.bottom + 6;
      if (y + th > window.innerHeight - 34) y = r.top - th - 6;
      tip.style.left = x + 'px';
      tip.style.top = Math.max(8, y) + 'px';
    }, 2000);
  }

  function confirmDelete(a) {
    var isG = isGroup(a);
    var n = isG ? membersOf(a).length : 0;
    var m = openModal(
      '<h3>' + (isG ? '删除分组' : '删除应用') + '</h3>' +
      '<p class="dim2" style="margin-top:-6px">' +
      (isG ? '删除分组「' + esc(a.name) + '」？组内 ' + n + ' 个应用会一并删除。只想拆散分组，请用右键的「取消分组」。'
            : '确定删除「' + esc(a.name) + '」？该操作不可撤销。') + '</p>' +
      '<div class="mfoot"><button class="btn ghost" data-x>取消</button>' +
      '<button class="btn danger-ghost" id="del-ok">删除</button></div>'
    );
    m.querySelector('#del-ok').onclick = function () {
      if (isG) {
        membersOf(a).forEach(function (mm) {
          if (pinned[mm.id]) { delete pinned[mm.id]; fire('close-widget', { appId: mm.id }); }
        });
        state.apps = state.apps.filter(function (x) { return x.g !== a.id && x.id !== a.id; });
      } else {
        if (pinned[a.id]) { delete pinned[a.id]; fire('close-widget', { appId: a.id }); }
        state.apps = state.apps.filter(function (x) { return x.id !== a.id; });
        var g = a.g ? findApp(a.g) : null;
        if (g) pruneGroup(g);
      }
      save(); render(); closeModal(); toast('已删除');
    };
  }

  /* ---------------- 打开应用 ---------------- */
  function resolveUrl(a) {
    if (a.kind === 'url') return Promise.resolve(a.target);
    var p = a.kind === 'folder' ? (a.entry || a.target) : a.target;
    if (hasBridge) {
      // 本地工具走专属 origin（https://app-<id>.local/...）：身份=稳定 id，数据按工具隔离
      return call('app-url', { appId: a.id }).then(function (r) {
        if (r && r.url) return r.url;
        if (!p) { toast('该应用缺少可打开的入口'); return null; }
        return call('to-file-url', { path: p }).then(function (r2) { return r2 && r2.url; }); // 兑底
      });
    }
    if (!p) { toast('该文件夹缺少 index.html'); return Promise.resolve(null); }
    return call('to-file-url', { path: p }).then(function (r) { return r && r.url; });
  }

  function openApp(a) {
    a.lastOpenedAt = Date.now();
    a.opens = (a.opens || 0) + 1;
    if (a.g) { var g = findApp(a.g); if (g) bumpGroup(g, a); }   // 组跟着成员一起上浮排序
    save();
    resolveUrl(a).then(function (url) {
      if (!url) return;
      if (a.openMode === 'window') { openWindowUrl(a, url); return; }
      // 工作台内打开 = 独立标签页：不整页跳转，工作台与已开页面状态全部保留
      if (hasBridge) {
        call('open-tab', { url: url, title: a.name, appId: a.id });
      } else {
        location.href = url;
      }
    });
  }

  function openWindow(a) {
    a.lastOpenedAt = Date.now();
    a.opens = (a.opens || 0) + 1;
    save();
    resolveUrl(a).then(function (url) {
      if (!url) return;
      openWindowUrl(a, url);
    });
  }

  function openWindowUrl(a, url) {
    if (hasBridge) {
      call('open-child', {
        url: url, title: a.name, appId: a.id,
        width: a.w || 0, height: a.h || 0
      });
    } else {
      window.open(url, '_blank');
    }
  }

  /* ---------------- 拖拽 HTML 文件：空白处=新增，压在应用上=升级该应用 ---------------- */
  function hasFiles(e) {
    var dt = e.dataTransfer;
    if (!dt) return false;
    if (dt.files && dt.files.length) return true;
    var t = dt.types;
    if (!t) return false;
    for (var i = 0; i < t.length; i++) if (t[i] === 'Files') return true;
    return false;
  }
  // 只有本地页面类（file/folder）才能被文件升级；网址应用拖上去仍走新增
  function upgradeCardAt(e) {
    var el = document.elementFromPoint(e.clientX, e.clientY);
    var c = el && el.closest ? el.closest('.card') : null;
    if (!c || !c.dataset.id) return null;
    var a = findApp(c.dataset.id);
    if (!a || (a.kind !== 'file' && a.kind !== 'folder')) return null;
    return { el: c, app: a };
  }
  var upMark = null;
  function markUpgrade(hit) {
    if (upMark && (!hit || upMark.el !== hit.el)) clearUpgrade();
    if (hit && !upMark) { hit.el.classList.add('upgrade'); upMark = hit; }
  }
  function clearUpgrade() {
    if (!upMark) return;
    upMark.el.classList.remove('upgrade');
    upMark = null;
  }

  document.addEventListener('dragover', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    var hit = upgradeCardAt(e);
    markUpgrade(hit);
    if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
  });
  document.addEventListener('dragleave', function (e) { if (!e.relatedTarget) clearUpgrade(); });
  document.addEventListener('drop', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    var hit = upgradeCardAt(e);
    clearUpgrade();
    var fs = e.dataTransfer && e.dataTransfer.files;
    if (!fs || !fs.length) return;
    var queue = [];
    for (var i = 0; i < fs.length; i++) {
      if (/\.(html?|mht|mhtml)$/i.test(fs[i].name)) queue.push(fs[i]);
    }
    if (!queue.length) { toast('只支持拖入 HTML 页面文件'); return; }
    if (hit) {
      if (queue.length > 1) toast('升级一次只认一个文件，已取第一个：' + queue[0].name);
      upgradeWithFile(hit.app, queue[0]);
      return;
    }
    dropQueue(queue, 0);
  });

  /* 升级入口一：把新版 HTML 拖到卡片上（网页拿不到绝对路径，所以正文由页面读出交给宿主） */
  function upgradeWithFile(app, f) {
    var reader = new FileReader();
    reader.onload = function () {
      var text = String(reader.result || '');
      call('compare-upgrade', { appId: app.id, text: text }).then(function (r) {
        if (!r || !r.ok) { toast('无法升级：' + ((r && r.error) || '未知错误')); return; }
        upgradeDialog(app, r.cmp, { text: text });
      });
    };
    reader.onerror = function () { toast('读不到这个文件'); };
    reader.readAsText(f);
  }
  /* 升级入口二：右键菜单选新文件（路径交给宿主读，GBK 页面也能兼容） */
  function upgradeFromPick(app) {
    call('pick-file').then(function (r) {
      if (!r || !r.ok) return;
      call('compare-upgrade', { appId: app.id, path: r.path }).then(function (c) {
        if (!c || !c.ok) { toast('无法升级：' + ((c && c.error) || '未知错误')); return; }
        upgradeDialog(app, c.cmp, { path: r.path });
      });
    });
  }

  function fmtSize(n) {
    n = Number(n) || 0;
    if (n > 1048576) return (n / 1048576).toFixed(1) + ' MB';
    if (n > 1024) return (n / 1024).toFixed(1) + ' KB';
    return n + ' B';
  }
  function upgradeDialog(app, c, payload) {
    var cur = c.cur || {}, nw = c.nw || {}, k = c.kept || {};
    var same = !!c.same, exists = !!c.exists;
    var diff;
    if (same) diff = '两份内容完全一致（指纹相同），其实不必更新。';
    else if (!exists) diff = '当前入口文件缺失（换机或误删），这次会把新版写回去，应用身份不变。';
    else {
      var parts = [];
      if (c.added) parts.push('新增 ' + c.added + ' 行');
      if (c.removed) parts.push('删除 ' + c.removed + ' 行');
      if (!parts.length) parts.push('内容有改动（行数相同）');
      diff = parts.join(' · ') + (c.firstDiff > 0 ? '，最早从第 ' + c.firstDiff + ' 行开始不同' : '');
    }
    function row(label, a, b) {
      return '<tr><th>' + label + '</th><td>' + a + '</td><td>' + b + '</td></tr>';
    }
    var m = openModal(
      '<h3>升级到新版本</h3>' +
      '<div class="hint" style="margin:0 0 12px">目标应用：<b>' + esc(app.name) + '</b>（' +
      (app.kind === 'folder' ? '本地文件夹' : '本地文件') + '）。确认后用新 HTML 覆盖入口，' +
      '应用身份与数据一律不动。</div>' +
      '<table class="cmp">' +
      row('页面标题', esc(cur.title || '（无标题）'), esc(nw.title || '（无标题）')) +
      row('自报版本', esc(cur.version || '—'), esc(nw.version || '—')) +
      row('体积', fmtSize(cur.size), fmtSize(nw.size)) +
      row('行数', String(cur.lines || 0), String(nw.lines || 0)) +
      row('修改时间', esc(cur.mtime || '—'), '<span class="dim">刚刚</span>') +
      row('指纹', esc(cur.hash || '—'), esc(nw.hash || '—')) +
      '</table>' +
      '<div class="cmpnote">' + esc(diff) + '</div>' +
      '<div class="keep">' +
      '<b>会保留</b>：localStorage / IndexedDB（按 origin 隔离）、导出成果' +
      (k.outputFiles ? ' ' + k.outputFiles + ' 个文件（' + fmtSize(k.outputBytes) + '）' : '') +
      '<br><b>会备份</b>：旧版本存到入口同级的 <code>.history</code>（滚动留最近 5 份）' +
      (k.dir ? '<br><span class="dim">数据目录：' + esc(k.dir) + '</span>' : '') +
      '</div>' +
      '<div class="mfoot"><button class="btn ghost" id="u-cancel">取消</button>' +
      '<button class="btn' + (same ? '' : ' primary') + '" id="u-ok">' +
      (same ? '仍然更新' : (exists ? '更新并保留数据' : '写回入口')) + '</button></div>', 'upgrade');
    m.querySelector('#u-cancel').onclick = closeModal;
    m.querySelector('#u-ok').onclick = function () {
      var arg = { appId: app.id };
      if (payload.path) arg.path = payload.path; else arg.text = payload.text;
      call('apply-upgrade', arg).then(function (u) {
        closeModal();
        if (!u || !u.ok) { toast('更新失败：' + ((u && u.error) || '未知错误')); return; }
        toast('已更新「' + app.name + '」，数据保留' + (u.backup ? '；旧版已备份' : ''));
      });
    };
    m.querySelector('#u-ok').focus();
  }

  function dropQueue(files, idx) {
    if (idx >= files.length) return;
    var f = files[idx];
    var reader = new FileReader();
    reader.onload = function () {
      var base = f.name.replace(/\.(html?|mht|mhtml)$/i, '');
      var text = String(reader.result || '');
      // 默认名与标签页统一：取网页内容的 <title>（无则 H1），拿不到才退回文件名
      var mt = /<title[^>]*>([\s\S]*?)<\/title>/i.exec(text) || /<h1[^>]*>([\s\S]*?)<\/h1>/i.exec(text);
      var dname = base;
      if (mt) {
        dname = mt[1].replace(/<[^>]*>/g, '')
          .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
          .replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&nbsp;/g, ' ')
          .replace(/\s+/g, ' ').trim().slice(0, 40) || base;
      }
      var kb = f.size > 1048576 ? (f.size / 1048576).toFixed(1) + ' MB'
                                 : Math.max(1, Math.round(f.size / 1024)) + ' KB';
      var m = openModal(
        '<h3>添加本地页面</h3>' +
        '<div class="field"><label>名称</label><input id="f-dname" type="text" value="' + esc(dname) + '"></div>' +
        '<div style="font-size:12px;color:var(--text-2);line-height:1.9;margin:6px 1px 14px">' +
        '文件：' + esc(f.name) + '（' + kb + '）<br>' +
        '类型：HTML 页面<br>' +
        '<span style="color:var(--dim)">确认后将保存副本到工作台数据目录并打开</span></div>' +
        '<div class="mfoot"><button class="btn ghost" id="f-dcancel">取消</button>' +
        '<button class="btn primary" id="f-dok">添加并打开</button></div>');
      m.querySelector('#f-dcancel').onclick = closeModal;
      m.querySelector('#f-dok').onclick = function () {
        var nm = (m.querySelector('#f-dname').value || '').trim() || dname;
        call('save-drop', { name: base + '.html', text: String(reader.result) }).then(function (r) {
          closeModal();
          if (!r || !r.ok) {
            toast('添加失败：' + ((r && r.error) || '未知错误'));
            return;
          }
          var a = {
            id: uid(), name: nm, kind: 'file', target: r.path, entry: null,
            openMode: 'inplace', tags: [], pinned: false, icon: null,
            createdAt: Date.now(), lastOpenedAt: 0, opens: 0, ord: nextTopOrd()
          };
          state.apps.push(a);
          save();
          render();
          toast('已添加「' + nm + '」');
          openApp(a);
          dropQueue(files, idx + 1);
        });
      };
      m.querySelector('#f-dname').focus();
      m.querySelector('#f-dname').select();
    };
    reader.readAsText(f);
  }

  /* ---------------- 分组（文件夹） ---------------- */
  /* 数据模型：分组就是 state.apps 里一条 kind:'group' 的条目，成员用 a.g 记下所属组 id。
     成员仍是普通条目，所以搜索/编辑/钉到桌面/持久化全部照用，不需要另存一份列表。 */
  function isGroup(a) { return !!a && a.kind === 'group'; }
  function membersOf(g) { return state.apps.filter(function (x) { return x.g === g.id; }).sort(byOrd); }
  function groupName(x, y) {
    var n = String(x.name || '应用').trim();
    if (/等$/.test(n)) return n;
    return (n.length > 8 ? n.slice(0, 8) : n) + ' 等';
  }
  function folderHtml(g) {
    var ms = membersOf(g), cells = '';
    for (var i = 0; i < 4; i++) {
      cells += ms[i] ? '<span class="fcell">' + miniHtml(ms[i]) + '</span>'
                     : '<span class="fcell empty"></span>';
    }
    return '<div class="tile folder"><div class="fcells">' + cells + '</div>' +
      (ms.length > 4 ? '<span class="fcount">' + ms.length + '</span>' : '') + '</div>';
  }
  function miniHtml(m) {
    var u = m.icon ? resolveIconSrc(m.icon) : null;
    if (u) return '<img src="' + esc(u) + '" alt="" onerror="this.remove()">';
    var c = hashColor(m.name);
    return '<span style="position:absolute;inset:0;display:flex;align-items:center;justify-content:center;' +
      'background:' + c + '26;color:' + c + '">' +
      esc((m.name || '?').trim().charAt(0).toUpperCase() || '•') + '</span>';
  }

  /* ---- 悬停展开：鼠标停在文件夹上自动弹开成员，带过冲弹动 ---- */
  var fly = { el: null, gid: null, pinned: false, timer: null };

  /* 鼠标是否还停在“浮层 + 它所属的文件夹”这块区域里。
     浮层现在盖在文件夹上方，两者合起来才算一个悬停区。 */
  function inFlyZone(x, y) {
    if (fly.el) {
      var q = fly.el.getBoundingClientRect();
      if (x >= q.left && x <= q.right && y >= q.top && y <= q.bottom) return true;
    }
    var el = fly.gid && nodes[fly.gid];
    if (el) {
      var r = el.getBoundingClientRect();
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return true;
    }
    return false;
  }

  function ensureFly() {
    if (fly.el) return fly.el;
    var f = document.createElement('div');
    f.className = 'fly';
    f.addEventListener('mouseenter', function () { clearTimeout(fly.timer); });
    f.addEventListener('mouseleave', function () { if (!fly.pinned) scheduleCloseFly(); });
    f.addEventListener('click', function (e) {
      var it = e.target.closest ? e.target.closest('.fitem') : null;
      if (!it || justDragged) return;
      var m = findApp(it.dataset.id);
      if (m) openApp(m);
    });
    f.addEventListener('contextmenu', function (e) {
      var it = e.target.closest ? e.target.closest('.fitem') : null;
      if (!it) return;
      e.preventDefault();
      var m = findApp(it.dataset.id);
      if (m) openCtx(e.clientX, e.clientY, m);
    });
    f.addEventListener('pointerdown', function (e) {
      var it = e.target.closest ? e.target.closest('.fitem') : null;
      if (it) { onItemPointerDown(e, it); return; }
      // 在展开的浮层空白处按下并拖动 = 移动该文件夹本体换位（浮层盖住文件夹时的可发现入口）
      var gid = f.dataset.gid;
      if (gid) {
        var gcard = document.querySelector('.card[data-id="' + gid + '"]');
        if (gcard) { closeFly(true); onItemPointerDown(e, gcard); }
      }
    });
    document.body.appendChild(f);
    fly.el = f;
    return f;
  }
  function hoverGroup(el, a) {
    if (drag) return;   // 拖拽途中划过文件夹不要弹开浮层，否则会挡住落点、干扰重排判定
    hideTip();
    if (fly.pinned && fly.gid !== a.id) { fly.pinned = false; closeFly(true); }  // 钉在别的组上：悬停新组就切过来
    if (fly.pinned) return;
    if (fly.gid === a.id) { clearTimeout(fly.timer); fly.timer = null; return; }  // 已经开着，别重复弹
    clearTimeout(fly.timer);
    fly.timer = setTimeout(function () { openFly(el, a); }, 120);   // 一小段延迟：路过时不要一路弹
  }
  function openFly(el, g) {
    var ms = membersOf(g);
    if (!ms.length) { closeFly(true); return; }
    var f = ensureFly();
    clearTimeout(fly.timer);
    if (f.dataset.gid !== g.id) {
      f.innerHTML = ms.map(function (m) {
        return '<div class="fitem" data-id="' + esc(m.id) + '" data-key="' + esc(m.id) + '" title="' + esc(m.name) + '">' +
          tileHtml(m) + '</div>';
      }).join('');
    }
    f.dataset.gid = g.id;
    // 列数跟着成员数走（最多 3 列）：只有两三个成员时不要留一整列空位
    f.style.gridTemplateColumns = 'repeat(' + Math.min(3, ms.length) + ',58px)';
    fly.gid = g.id;
    positionFly(f, el);
    openMark(el);
    f.classList.add('open');
  }
  function positionFly(f, el) {
    var r = el.getBoundingClientRect();
    var fw = f.offsetWidth, fh = f.offsetHeight;
    // 在文件夹所在位置的正上方展开（盖住文件夹本身），而不是吊在它下面
    var x = r.left + r.width / 2 - fw / 2;
    var y = r.top + r.height / 2 - fh / 2;
    x = Math.min(Math.max(8, x), Math.max(8, window.innerWidth - fw - 8));
    y = Math.min(Math.max(8, y), Math.max(8, window.innerHeight - fh - 8));
    f.style.left = x + 'px';
    f.style.top = y + 'px';
  }
  function openMark(el) {
    Array.prototype.forEach.call(document.querySelectorAll('.card.grp.open'), function (x) { x.classList.remove('open'); });
    if (el) el.classList.add('open');
  }
  function closeFly(now) {
    clearTimeout(fly.timer);
    fly.timer = null;      // 置回 null，pointermove 才能重新排定下一次收起
    if (now) fly.pinned = false;
    if (fly.pinned) return;
    if (fly.el) {
      fly.el.classList.remove('open');
      if (now) fly.el.dataset.gid = '';
    }
    fly.gid = null;
    Array.prototype.forEach.call(document.querySelectorAll('.card.grp.open'), function (x) { x.classList.remove('open'); });
  }
  function scheduleCloseFly() {
    if (fly.pinned || !fly.gid || drag) return;   // 正在拖拽时不自动收起（成员需在浮层内重排）
    clearTimeout(fly.timer);
    fly.timer = setTimeout(function () { closeFly(true); }, 200);   // 从文件夹移向成员时不致于半路收起
  }
  /* 光靠 mouseleave 收不干净：浮层盖住文件夹后，鼠标往往同时离开两者却不触发收起。
     用指针位置统一判定 —— 不在“浮层+文件夹”区域内就排定收起，回到区域内立即取消。 */
  document.addEventListener('pointermove', function (e) {
    if (!fly.gid || fly.pinned || drag) return;
    if (inFlyZone(e.clientX, e.clientY)) { clearTimeout(fly.timer); fly.timer = null; }
    else if (fly.timer === null) scheduleCloseFly();
  });
  function toggleGroupOpen(el, g) {
    if (fly.pinned && fly.gid === g.id) { fly.pinned = false; closeFly(true); return; }
    fly.pinned = true;      // 单击 = 钉住展开（方便点选与拖出），再点一下收起
    openFly(el, g);
  }

  /* ---- 拖叠成组：把一张卡拖到另一张卡上 ---- */
  var drag = null;
  var justDragged = false;

  function onItemPointerDown(e, el) {
    if (e.button !== 0 || drag) return;
    if (e.pointerType === 'touch') return;   // 触屏上手指要留给滚动，不抢成组拖拽
    var id = el.dataset.id || el.dataset.key;
    if (!findApp(id)) return;
    drag = {
      id: id, el: el, x0: e.clientX, y0: e.clientY, moved: false,
      ghost: null, over: null, home: null
    };
    document.addEventListener('pointermove', onDragMove, true);
    document.addEventListener('pointerup', onDragUp, true);
    document.addEventListener('pointercancel', onDragUp, true);
  }
  function onDragMove(e) {
    if (!drag) return;
    if (!drag.moved) {
      if (Math.abs(e.clientX - drag.x0) + Math.abs(e.clientY - drag.y0) < 9) return;
      drag.moved = true;
      justDragged = true;
      var r = drag.el.getBoundingClientRect();
      drag.home = { left: r.left, top: r.top, w: r.width };
      var g = document.createElement('div');
      g.className = 'drag-ghost';
      g.innerHTML = drag.el.innerHTML;      // 卡片/成员里都是 .tile，直接拿来做拖影
      document.body.appendChild(g);
      drag.ghost = g;
      drag.el.classList.add('dragging');
      hideTip();
      closeCtx();
      var ds = findApp(drag.id);
      if (!ds || !ds.g) closeFly(true);   // 顶层拖拽收起浮层；拖组成员时保持浮层开启以便组内重排
    }
    e.preventDefault();
    var gw = drag.ghost.offsetWidth || 68;
    drag.ghost.style.left = (e.clientX - gw / 2) + 'px';
    drag.ghost.style.top = (e.clientY - gw / 2) + 'px';
    drag.ghost.style.transform = 'scale(1.08) rotate(-2deg)';
    hitTest(e.clientX, e.clientY);
  }
  function hitTest(x, y) {
    var src = findApp(drag.id);
    if (!src) return;
    var el = document.elementFromPoint(x, y);
    clearDropMarks();
    drag.over = null;
    if (!el || !el.closest) return;

    if (src.g) {
      // 成员：1) 浮层内拖到另一成员左/右半 = 组内重排
      var fitem = el.closest('.fitem');
      if (fitem && fitem.dataset.id && fitem.dataset.id !== src.id) {
        var fr = fitem.getBoundingClientRect();
        var fbefore = (fr.width ? (x - fr.left) / fr.width : 0.5) < 0.5;
        fitem.classList.add(fbefore ? 'drop-before' : 'drop-after');
        drag.over = { mode: 'insert', id: fitem.dataset.id, before: fbefore };
        return;
      }
      // 2) 拖到别的卡片上 = 加入其所在组 / 与它成组
      var fcard = el.closest('.card');
      if (fcard && fcard.dataset.id && fcard.dataset.id !== src.id) {
        var ft = findApp(fcard.dataset.id);
        if (ft) {
          fcard.classList.add('drop');
          fcard.dataset.drop = isGroup(ft) ? '加入' : '成组';
          drag.over = { mode: 'group', id: ft.id };
          return;
        }
      }
      // 3) 拖到浮层以外的空白 = 移出分组（拖回工作台）：只在源文件夹上给一圈轻红边
      if (!el.closest('.fly') && els.main && els.main.contains(el)) {
        var gcard = document.querySelector('.card[data-id="' + src.g + '"]');
        if (gcard) gcard.classList.add('drop-out');
        drag.over = { mode: 'out' };
      }
      return;
    }

    // 顶层：落在另一张卡片上，按水平位置三区区分意图
    var tgtEl = el.closest('.card');
    if (tgtEl && tgtEl.dataset.id && tgtEl.dataset.id !== src.id) {
      var r = tgtEl.getBoundingClientRect();
      var rel = r.width ? (x - r.left) / r.width : 0.5;
      var t = findApp(tgtEl.dataset.id);
      if (!t) return;
      if (rel < 0.30) {
        tgtEl.classList.add('drop-before');
        drag.over = { mode: 'insert', id: t.id, before: true };
      } else if (rel > 0.70) {
        tgtEl.classList.add('drop-after');
        drag.over = { mode: 'insert', id: t.id, before: false };
      } else {
        // 压在卡片正中 = 成组/加入（沿用原逻辑）
        tgtEl.classList.add('drop');
        tgtEl.dataset.drop = isGroup(t) ? '加入' : '成组';
        drag.over = { mode: 'group', id: t.id };
      }
      return;
    }
    // 顶层：拖到真正空白（不在任何卡片/浮层上）= 移到末尾；用末尾一张卡的细插入线提示，不再画整网格大框
    if (!el.closest('.card') && !el.closest('.fly') && els.main && els.main.contains(el)) {
      drag.over = { mode: 'end' };
      var cards = els.grid.querySelectorAll('.card');
      var last = cards.length ? cards[cards.length - 1] : null;
      if (last && last.dataset.id !== src.id) last.classList.add('drop-after');
    }
  }
  function clearDropMarks() {
    Array.prototype.forEach.call(document.querySelectorAll(
      '.card.drop,.card.drop-before,.card.drop-after,.card.drop-out,.fitem.drop-before,.fitem.drop-after'), function (e) {
      e.classList.remove('drop', 'drop-before', 'drop-after', 'drop-out');
      if (e.dataset.drop) delete e.dataset.drop;
    });
  }
  function onDragUp() {
    document.removeEventListener('pointermove', onDragMove, true);
    document.removeEventListener('pointerup', onDragUp, true);
    document.removeEventListener('pointercancel', onDragUp, true);
    if (!drag) return;
    var d = drag;
    drag = null;
    clearDropMarks();
    if (d.el) d.el.classList.remove('dragging');
    if (!d.moved) { justDragged = false; return; }
    var ok = false;
    var src = findApp(d.id);
    if (src && d.over) {
      if (d.over.mode === 'group') {
        ok = dropOn(src, findApp(d.over.id));
      } else if (d.over.mode === 'insert') {
        ok = reorderWithin(src, d.over.id, d.over.before);
        if (ok) { save(); render(); }
      } else if (d.over.mode === 'out') {
        if (src.g) { leaveGroup(src); ok = true; }
      } else if (d.over.mode === 'end') {
        if (!src.g) { appendToEnd(src); save(); render(); ok = true; }
      }
    }
    if (d.ghost) {
      var gh = d.ghost;
      if (ok) {
        gh.style.transition = 'transform .18s ease,opacity .18s ease';
        gh.style.transform = 'scale(.35)';
        gh.style.opacity = '0';
        setTimeout(function () { gh.remove(); }, 200);
      } else {
        // 没落到有效目标：弹回原位（过冲曲线，看起来就是一次回抽）
        var r = d.el.getBoundingClientRect();
        gh.style.transition = 'left .3s cubic-bezier(.3,1.5,.55,1),top .3s cubic-bezier(.3,1.5,.55,1),transform .3s';
        gh.style.left = r.left + 'px';
        gh.style.top = r.top + 'px';
        gh.style.transform = 'none';
        setTimeout(function () { gh.remove(); }, 320);
      }
    }
    setTimeout(function () { justDragged = false; }, 0);
  }

  /* ---- 分组操作 ---- */
  function dropOn(src, tgt) {
    if (!src || !tgt || src.id === tgt.id) return false;
    if (src.g && src.g === tgt.g) return false;          // 同组内互拖不算成组
    if (isGroup(tgt)) {
      if (src.g === tgt.id) return false;
      if (isGroup(src)) mergeInto(tgt, src);
      else { detach(src); src.g = tgt.id; bumpGroup(tgt, src); toast('已加入「' + tgt.name + '」'); }
      save(); render(); return true;
    }
    if (isGroup(src)) {
      detach(tgt); tgt.g = src.id; bumpGroup(src, tgt);
      toast('已加入「' + src.name + '」');
      save(); render(); return true;
    }
    makeGroup(tgt, src);
    save(); render(); return true;
  }
  function makeGroup(x, y) {
    var g = {
      id: 'g' + uid(), kind: 'group', name: groupName(x, y), openMode: 'inplace', tags: [],
      pinned: !!(x.pinned || y.pinned), icon: null, createdAt: Date.now(),
      lastOpenedAt: Math.max(x.lastOpenedAt || 0, y.lastOpenedAt || 0),
      opens: (x.opens || 0) + (y.opens || 0),
      ord: Math.min(x.ord || nextTopOrd(), y.ord || nextTopOrd())
    };
    detach(x); detach(y);
    state.apps.push(g);
    x.g = g.id; y.g = g.id;
    toast('已分组「' + g.name + '」：悬停可展开，拖到另一张卡上可继续合并');
    return g;
  }
  function mergeInto(dst, src) {
    membersOf(src).forEach(function (m) { m.g = dst.id; });
    state.apps = state.apps.filter(function (x) { return x.id !== src.id; });
    toast('已合并到「' + dst.name + '」');
  }
  function detach(a) {
    if (!a.g) return;
    var old = findApp(a.g);
    a.g = null;
    if (old) pruneGroup(old);
  }
  /* 组里不足 2 个就没必要存在了，自动解散 */
  function pruneGroup(g) {
    var ms = membersOf(g);
    if (ms.length >= 2) { bumpGroup(g); return; }
    ms.forEach(function (m) { m.g = null; });
    state.apps = state.apps.filter(function (x) { return x.id !== g.id; });
  }
  function bumpGroup(g, m) {
    var t = m ? (m.lastOpenedAt || 0) : 0;
    if (!m) membersOf(g).forEach(function (x) { t = Math.max(t, x.lastOpenedAt || 0); });
    g.lastOpenedAt = Math.max(g.lastOpenedAt || 0, t);
  }
  function leaveGroup(a) {
    var g = findApp(a.g);
    if (!g) { a.g = null; save(); render(); return; }
    a.g = null;
    pruneGroup(g);
    save(); render();
    toast('已移出「' + g.name + '」');
  }
  function ungroup(g) {
    membersOf(g).forEach(function (m) { m.g = null; });
    state.apps = state.apps.filter(function (x) { return x.id !== g.id; });
    save(); render();
    toast('已取消分组');
  }
  function openAll(g) {
    membersOf(g).forEach(function (m) { openApp(m); });
  }
  function renameGroup(g) {
    var m = openModal(
      '<h3>重命名分组</h3>' +
      '<div class="field" style="margin-top:0"><input id="g-name" type="text" maxlength="40" value="' + esc(g.name) + '"></div>' +
      '<div class="mfoot"><button class="btn ghost" data-x>取消</button>' +
      '<button class="btn primary" id="g-ok">保存</button></div>');
    var inp = m.querySelector('#g-name');
    inp.focus(); inp.select();
    function ok() {
      var v = inp.value.trim();
      if (v) { g.name = v; save(); render(); toast('已重命名'); }
      closeModal();
    }
    m.querySelector('#g-ok').onclick = ok;
    inp.addEventListener('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); ok(); } });
  }
  /* 不用拖拽也能把应用放进组：右键菜单走这个面板 */
  function pickGroupDialog(a) {
    var gs = state.apps.filter(function (x) { return isGroup(x) && x.id !== a.g; });
    var html = '<h3>加入分组</h3>' +
      '<p class="dim2" style="margin-top:-6px">把「' + esc(a.name) + '」放进哪个分组？</p>' +
      '<div class="field" style="margin-top:12px">';
    gs.forEach(function (g) {
      html += '<button class="btn ghost gpick" data-id="' + esc(g.id) + '" style="display:block;width:100%;text-align:left;margin-bottom:6px">' +
        esc(g.name) + ' · ' + membersOf(g).length + ' 个</button>';
    });
    html += '<button class="btn ghost gpick" data-id="" style="display:block;width:100%;text-align:left">＋ 新建分组</button></div>' +
      '<div class="mfoot"><button class="btn ghost" data-x>取消</button></div>';
    var m = openModal(html);
    Array.prototype.forEach.call(m.querySelectorAll('.gpick'), function (b) {
      b.onclick = function () {
        var g = b.dataset.id ? findApp(b.dataset.id) : null;
        if (g) { detach(a); a.g = g.id; bumpGroup(g, a); toast('已加入「' + g.name + '」'); }
        else {
          var ng = { id: 'g' + uid(), kind: 'group', name: String(a.name || '应用').trim() + ' 等', openMode: 'inplace',
                     tags: [], pinned: false, createdAt: Date.now(), lastOpenedAt: a.lastOpenedAt || 0, opens: a.opens || 0, ord: a.ord || nextTopOrd() };
          state.apps.push(ng);
          detach(a); a.g = ng.id;
          toast('已新建「' + ng.name + '」');
        }
        closeModal(); save(); render();
      };
    });
  }

  /* ---------------- 弹窗 ---------------- */
  // 遮罩只盖得住页面，盖不住宿主窗体四周 6px 的缩放边条；不处理就会在遮罩外圈
  // 留下一圈白线（边条是窗口底色）。开/关时通知宿主把边条刷成遮罩色。
  function openModal(html, kind) {
    els.modalRoot.innerHTML = '<div class="modal"><div class="modal-body">' + html + '</div></div>';
    els.modalRoot.classList.add('open');
    modalKind = kind || '';
    fire('set-dim', { on: true });
    els.modalRoot.onclick = function (e) { if (e.target === els.modalRoot) closeModal(); };
    var x = els.modalRoot.querySelector('[data-x]');
    if (x) x.onclick = closeModal;
    return els.modalRoot.querySelector('.modal');
  }
  function closeModal() {
    var was = els.modalRoot.classList.contains('open');
    var kind = modalKind;
    els.modalRoot.classList.remove('open');
    els.modalRoot.innerHTML = '';
    els.modalRoot.onclick = null;
    modalKind = '';
    if (was) fire('set-dim', { on: false });
    if (was && kind === 'settings') fire('settings-closed');
    if (was && kind === 'settings' && !hasBridge) document.documentElement.classList.remove('solo');
  }
  function modalOpen() { return els.modalRoot.classList.contains('open'); }
  var modalKind = '';   // 当前弹窗种类，供标题栏设置/添加按钮判断是否撞车

  function segWire(seg, onChange) {
    seg.addEventListener('click', function (e) {
      var b = e.target.closest('button');
      if (!b || b.classList.contains('on')) return;
      seg.querySelectorAll('button').forEach(function (x) { x.classList.remove('on'); });
      b.classList.add('on');
      onChange(b.dataset.v);
    });
  }
  function segValue(seg) {
    var b = seg.querySelector('button.on');
    return b ? b.dataset.v : null;
  }
  function segSet(seg, v) {
    seg.querySelectorAll('button').forEach(function (b) {
      b.classList.toggle('on', b.dataset.v === v);
    });
  }

  /* ---------------- 添加 / 编辑应用 ---------------- */
  function appFormHtml(pending) {
    return '' +
      '<h3>' + (pending.editing ? '编辑应用' : '添加应用') + '</h3>' +
      '<div class="field" style="margin-top:0"><label>类型</label>' +
        '<div class="seg" id="seg-kind">' +
          '<button data-v="url">网址</button>' +
          '<button data-v="file">本地文件</button>' +
          '<button data-v="folder">本地文件夹</button>' +
        '</div></div>' +
      '<div class="field" id="row-url"><label>网址</label>' +
        '<input type="text" id="f-url" placeholder="https://…" spellcheck="false">' +
        '<p class="hint">省略 https:// 时自动补全；自动获取网站图标</p></div>' +
      '<div class="field hidden" id="row-file"><label>HTML 文件</label>' +
        '<button class="btn ghost" id="f-pickfile">选择文件…</button><span class="path" id="f-filepath"></span>' +
        '<p class="hint">选择一个 .html 单文件应用</p></div>' +
      '<div class="field hidden" id="row-folder"><label>应用文件夹</label>' +
        '<button class="btn ghost" id="f-pickdir">选择文件夹…</button><span class="path" id="f-dirpath"></span>' +
        '<p class="hint">启动时打开文件夹中的 index.html</p></div>' +
      '<div class="field hidden" id="row-update"><label>更新 HTML（保留数据）</label>' +
        '<button class="btn ghost" id="f-update">选择新版 HTML…</button><span class="path" id="f-updatepath"></span>' +
        '<p class="hint">仅替换入口文件；该工具已产生的数据挂在其专属身份下，升级不会丢失</p></div>' +
      '<div class="field"><label>名称</label>' +
        '<input type="text" id="f-name" placeholder="应用名称" maxlength="40"></div>' +
      '<div class="field"><label>标签（逗号分隔）</label>' +
        '<input type="text" id="f-tags" placeholder="例如：工具, 效率" maxlength="60"></div>' +
      '<div class="two">' +
        '<div class="field"><label>打开方式</label>' +
          '<div class="seg" id="seg-mode">' +
            '<button data-v="inplace">工作台内</button>' +
            '<button data-v="window">独立窗口</button>' +
          '</div></div>' +
        '<div class="field"><label>其他</label>' +
          '<label class="switch"><input type="checkbox" id="f-pin"><span class="track"></span><span class="slbl">置顶显示</span></label>' +
        '</div>' +
      '</div>' +
      '<div class="field"><label>自定义图标（可选，优先于网站 favicon）</label>' +
        '<div class="iconrow"><button class="btn ghost" id="f-pickicon">选择图片…</button>' +
        '<button class="btn ghost" id="f-genicon">✨ 生成图标…</button>' +
        '<button class="btn ghost" id="f-clelicon">清除</button>' +
        '<span class="path" id="f-iconpath"></span></div></div>' +
      '<div class="mfoot' + (pending.editing ? ' split' : '') + '">' +
        (pending.editing ? '<button class="btn danger-ghost" id="f-del">删除应用</button>' : '') +
        '<div class="group">' +
          '<button class="btn ghost" data-x>取消</button>' +
          '<button class="btn primary" id="f-ok">' + (pending.editing ? '保存' : '添加') + '</button>' +
        '</div>' +
      '</div>';
  }

  function bindAppForm(m, pending) {
    var segKind = m.querySelector('#seg-kind');
    var segMode = m.querySelector('#seg-mode');
    var rowUrl = m.querySelector('#row-url');
    var rowFile = m.querySelector('#row-file');
    var rowFolder = m.querySelector('#row-folder');
    var rowUpdate = m.querySelector('#row-update');

    function showRow(kind) {
      rowUrl.classList.toggle('hidden', kind !== 'url');
      rowFile.classList.toggle('hidden', kind !== 'file');
      rowFolder.classList.toggle('hidden', kind !== 'folder');
      // “更新 HTML”只在编辑已有的本地工具时出现（网址类无需）
      if (rowUpdate) rowUpdate.classList.toggle('hidden',
        !(pending.editing && hasBridge && (kind === 'file' || kind === 'folder')));
    }
    showRow(pending.kind);
    segSet(segKind, pending.kind);
    segSet(segMode, pending.openMode || 'inplace');
    segWire(segKind, function (v) { pending.kind = v; showRow(v); });
    segWire(segMode, function (v) { pending.openMode = v; });

    if (pending.kind === 'url' && pending.target) m.querySelector('#f-url').value = pending.target;
    if (pending.kind === 'file') m.querySelector('#f-filepath').textContent = pending.target || '';
    if (pending.kind === 'folder') m.querySelector('#f-dirpath').textContent = pending.target || '';
    if (pending.name) m.querySelector('#f-name').value = pending.name;
    if (pending.tagsStr) m.querySelector('#f-tags').value = pending.tagsStr;
    m.querySelector('#f-pin').checked = !!pending.pinned;
    if (pending.icon) m.querySelector('#f-iconpath').textContent = /^data:/i.test(pending.icon) ? '已生成图标' : pending.icon;

    m.querySelector('#f-pickfile').onclick = function () {
      call('pick-file').then(function (r) {
        if (r && r.ok) {
          pending.target = r.path;
          if (!m.querySelector('#f-name').value) m.querySelector('#f-name').value = r.name || '';
          m.querySelector('#f-filepath').textContent = r.path;
        }
      });
    };
    m.querySelector('#f-pickdir').onclick = function () {
      call('pick-folder').then(function (r) {
        if (r && r.ok) {
          pending.target = r.path;
          pending.entry = r.entry || null;
          if (!m.querySelector('#f-name').value) m.querySelector('#f-name').value = r.name || r.path.split('\\').pop();
          m.querySelector('#f-dirpath').textContent = r.path + (r.entry ? '（index.html ✓）' : '（缺少 index.html）');
        }
      });
    };
    m.querySelector('#f-pickicon').onclick = function () {
      call('pick-image').then(function (r) {
        if (r && r.ok) {
          pending.icon = r.path;
          m.querySelector('#f-iconpath').textContent = r.path;
        }
      });
    };
    m.querySelector('#f-genicon').onclick = function () {
      var nm = (m.querySelector('#f-name').value || pending.name || '').trim();
      var seed, ws = nm.split(/\s+/).filter(Boolean);
      if (/[\u4e00-\u9fa5]/.test(nm)) seed = nm.replace(/[^\u4e00-\u9fa5]/g, '').slice(0, 2);
      else if (ws.length > 1) seed = ws[0].charAt(0) + ws[1].charAt(0);
      else seed = nm.slice(0, 1);
      if (!seed) seed = 'A';
      openIconGen(this, { text: seed, bg: hashColor(nm || seed) }, function (dataUrl) {
        pending.icon = dataUrl;
        m.querySelector('#f-iconpath').textContent = '已生成图标';
      });
    };
    var updBtn = m.querySelector('#f-update');
    if (updBtn) updBtn.onclick = function () {
      call('pick-file').then(function (r) {
        if (!r || !r.ok) return;
        call('update-app-source', { appId: pending.editing.id, path: r.path }).then(function (u) {
          if (u && u.ok) { m.querySelector('#f-updatepath').textContent = r.path; toast('已更新，数据保留'); }
          else toast('更新失败：' + ((u && u.error) || '未知错误'));
        });
      });
    };
    m.querySelector('#f-clelicon').onclick = function () {
      pending.icon = null;
      m.querySelector('#f-iconpath').textContent = '';
    };

    var delBtn = m.querySelector('#f-del');
    if (delBtn) {
      delBtn.onclick = function () {
        state.apps = state.apps.filter(function (x) { return x.id !== pending.editing.id; });
        save(); render(); closeModal(); toast('已删除');
      };
    }

    function submit() {
      var name = m.querySelector('#f-name').value.trim();
      var kind = pending.kind;
      var target = null, entry = null;

      if (kind === 'url') {
        target = m.querySelector('#f-url').value.trim();
        if (!target) { toast('请输入网址'); return; }
        if (!/^https?:\/\//i.test(target)) target = 'https://' + target;
        if (!name) {
          try { name = new URL(target).hostname.replace(/^www\./, ''); } catch (e) { }
        }
      } else if (kind === 'file') {
        target = pending.target;
        if (!target) { toast('请先选择 HTML 文件'); return; }
      } else {
        target = pending.target;
        entry = pending.entry || null;
        if (!target) { toast('请先选择文件夹'); return; }
      }

      name = name || '未命名应用';
      var mode = segValue(segMode) || 'inplace';
      var tags = m.querySelector('#f-tags').value.split(/[,，]/)
        .map(function (s) { return s.trim(); }).filter(Boolean);
      var pinned = m.querySelector('#f-pin').checked;

      if (pending.editing) {
        var a = pending.editing;
        a.name = name; a.kind = kind; a.target = target;
        a.entry = entry; a.openMode = mode;
        a.tags = tags; a.pinned = pinned; a.icon = pending.icon || null;
      } else {
        state.apps.push({
          id: uid(), name: name, kind: kind, target: target, entry: entry,
          openMode: mode, tags: tags, pinned: pinned, icon: pending.icon || null,
          createdAt: Date.now(), lastOpenedAt: 0, opens: 0, ord: nextTopOrd()
        });
      }
      save(); render(); closeModal();
      toast(pending.editing ? '已保存' : '已添加「' + name + '」');
    }

    m.querySelector('#f-ok').onclick = submit;
    m.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && e.target.tagName === 'INPUT' && e.target.type === 'text') {
        e.preventDefault(); submit();
      }
    });

    if (pending.kind === 'url') m.querySelector('#f-url').focus();
    else m.querySelector('#f-name').focus();
  }

  function addDialog() {
    var pending = {
      kind: 'url', target: null, entry: null, name: '',
      openMode: state.settings.defaultOpenMode || 'inplace',
      tagsStr: '', pinned: false, icon: null, editing: null
    };
    var m = openModal(appFormHtml(pending), 'app');
    bindAppForm(m, pending);
  }

  function editDialog(a) {
    var pending = {
      kind: a.kind, target: a.target, entry: a.entry || null,
      name: a.name, openMode: a.openMode || 'inplace',
      tagsStr: (a.tags || []).join(', '), pinned: !!a.pinned, icon: a.icon || null,
      editing: a
    };
    var m = openModal(appFormHtml(pending), 'app');
    bindAppForm(m, pending);
  }

  /* ---------------- 设置中心 ---------------- */
  function settingsDialog(overlay) {
    var s = state.settings;
    // overlay：宿主把首页 WebView 透明盖在当前标签上，只留弹窗，当前页面透出成为背景
    if (overlay) document.documentElement.classList.add('solo');
    var m = openModal(
      '<h3>设置</h3>' +
      '<div class="set-sec">外观</div>' +
      '<div class="field" style="margin-top:8px"><label>主题</label>' +
        '<div class="seg" id="s-theme">' +
          '<button data-v="dark">深色</button>' +
          '<button data-v="light">浅色</button>' +
          '<button data-v="auto">跟随系统</button>' +
        '</div></div>' +
      '<div class="field"><label>网址应用图标</label>' +
        '<label class="switch"><input type="checkbox" id="s-fav"><span class="track"></span>' +
        '<span class="slbl">自动获取网站 favicon（失败时回退字母块）</span></label></div>' +
      '<div class="set-sec">行为</div>' +
      '<div class="field" style="margin-top:8px"><label>关闭主窗口时</label>' +
        '<div class="seg" id="s-close">' +
          '<button data-v="exit">退出程序</button>' +
          '<button data-v="tray">最小化到托盘</button>' +
        '</div></div>' +
      '<div class="field"><label class="switch"><input type="checkbox" id="s-hotkey"><span class="track"></span>' +
        '<span class="slbl">全局呼出热键（任意界面按下即可调出页坞）</span></label></div>' +
      '<div class="field"><label>呼出组合键</label><div class="hkrow">' +
        '<button class="btn ghost" id="s-hkbtn">Ctrl + Alt + D</button>' +
        '<span class="dim2" id="s-hkhint">点击按钮后按下新组合（需含 Ctrl 或 Alt）</span></div></div>' +
      '<div class="field"><label class="switch"><input type="checkbox" id="s-autostart"><span class="track"></span>' +
        '<span class="slbl">开机自动启动（配合“最小化到托盘”常驻，未启动时热键也能冷启动唤起）</span></label></div>' +
      '<div class="field"><label>新应用默认打开方式</label>' +
        '<div class="seg" id="s-mode">' +
          '<button data-v="inplace">工作台内</button>' +
          '<button data-v="window">独立窗口</button>' +
        '</div></div>' +
      '<div class="set-sec">数据</div>' +
      '<div class="field" style="margin-top:8px"><div class="datarow">' +
        '<span class="dim2" id="s-datadir"></span>' +
        '<button class="btn ghost" id="s-reveal">打开目录</button></div></div>' +
      '<div class="field"><label>备份与迁移</label>' +
        '<div class="hkrow"><button class="btn" id="s-exp-full">导出整包</button>' +
        '<button class="btn ghost" id="s-exp-lite">导出轻量</button>' +
        '<button class="btn ghost" id="s-import">导入备份…</button></div>' +
        '<p class="hint">整包=清单+各工具源与数据（换机完整迁移）；轻量=清单+源（不含工具缓存）。导入后重启一次可完整还原工具数据。</p></div>' +
      '<div class="set-sec">开发</div>' +
      '<div class="field" style="margin-top:8px"><div class="datarow">' +
        '<span class="dim2">界面生成规范（UI / 布局标准，新增工具时参考）</span>' +
        '<button class="btn ghost" id="s-spec">打开文件位置</button></div></div>' +
      '<div class="mfoot split">' +
        '<span class="dim2 ver">页坞 PageDock v' + esc(info.version) + '</span>' +
        '<a class="dim2" href="#" id="s-credit">© 2026 Maitool · MIT License</a>' +
        '<div class="group"><button class="btn ghost" data-x>关闭</button></div>' +
      '</div>',
      'settings'
    );

    segSet(m.querySelector('#s-theme'), s.theme || 'auto');
    segWire(m.querySelector('#s-theme'), function (v) {
      s.theme = v; save(); applyTheme();
    });
    var fav = m.querySelector('#s-fav');
    fav.checked = s.favicons !== false;
    fav.onchange = function () { s.favicons = fav.checked; save(); render(); };
    segSet(m.querySelector('#s-close'), s.closeMode || 'exit');
    segWire(m.querySelector('#s-close'), function (v) {
      s.closeMode = v; save();
      fire('set-close-mode', { mode: v });
      toast(v === 'tray' ? '关闭主窗口将最小化到托盘' : '关闭主窗口将退出程序');
    });
    var hk = m.querySelector('#s-hotkey');
    hk.checked = s.hotkey !== false;
    hk.onchange = function () {
      s.hotkey = hk.checked; save();
      pushHotkey();
      toast(hk.checked ? '呼出热键已开启' : '呼出热键已关闭');
    };

    // 自定义组合键录入
    var hkBtn = m.querySelector('#s-hkbtn');
    var hkHint = m.querySelector('#s-hkhint');
    var curMod = s.hotkeyMod != null ? s.hotkeyMod : 0x3;
    var curVk = s.hotkeyVk != null ? s.hotkeyVk : 0x44;
    function refreshHkBtn() { if (hkBtn && !recording) hkBtn.textContent = hotkeyLabel(curMod, curVk); }
    var recording = false;
    function stopRec(msg) {
      recording = false;
      document.removeEventListener('keydown', onRec, true);
      refreshHkBtn();
      if (msg && hkHint) hkHint.textContent = msg;
    }
    function startRec() {
      recording = true;
      if (hkBtn) hkBtn.textContent = '按下新组合…（Esc 取消）';
      document.addEventListener('keydown', onRec, true);
    }
    function onRec(e) {
      if (!modalOpen()) { stopRec(); return; }
      e.preventDefault(); e.stopPropagation();
      if (e.key === 'Escape') { stopRec('已取消录入'); return; }
      if (['Control', 'Alt', 'Shift', 'Meta'].indexOf(e.key) >= 0) return; // 等真正的键
      var kv = evKeyToInfo(e);
      if (!kv.vk) return;
      if (!(kv.mod & 0x3)) { if (hkHint) hkHint.textContent = '需包含 Ctrl 或 Alt'; return; }
      curMod = kv.mod; curVk = kv.vk;
      s.hotkeyMod = curMod; s.hotkeyVk = curVk; s.hotkey = true; hk.checked = true;
      save(); pushHotkey();
      stopRec('已设为 ' + hotkeyLabel(curMod, curVk) + '（与其他程序冲突会自动回退 Ctrl+Alt+D）');
    }
    if (hkBtn) { refreshHkBtn(); hkBtn.onclick = function () { recording ? stopRec() : startRec(); }; }

    segSet(m.querySelector('#s-mode'), s.defaultOpenMode || 'inplace');
    segWire(m.querySelector('#s-mode'), function (v) {
      s.defaultOpenMode = v; save();
    });
    m.querySelector('#s-datadir').textContent = info.dataDir || '（预览）';
    m.querySelector('#s-reveal').onclick = function () {
      call('reveal', { path: info.dataDir });
    };
    m.querySelector('#s-spec').onclick = function () {
      call('reveal', { path: (info.exeDir || '').replace(/[\\\/]+$/, '') + '\\spec\\pagedock-web-ui.md' });
    };
    m.querySelector('#s-credit').onclick = function (e) {
      e.preventDefault();
      call('open-external', { url: 'https://github.com/Maitool/PageDock' });
    };

    // 开机自启（以注册表为准）
    var auto = m.querySelector('#s-autostart');
    call('get-autostart').then(function (r) { if (r && r.ok) auto.checked = !!r.on; });
    auto.onchange = function () {
      call('set-autostart', { on: auto.checked }).then(function (r) {
        if (!r || !r.ok) { auto.checked = !auto.checked; toast('设置自启失败：' + ((r && r.error) || '')); return; }
        auto.checked = !!r.on;
        if (auto.checked && (s.closeMode || 'exit') === 'exit') {
          // 常驻：自动切为“关闭最小化到托盘”，保证退出到后台后热键仍能唤起
          s.closeMode = 'tray'; save();
          segSet(m.querySelector('#s-close'), 'tray');
          fire('set-close-mode', { mode: 'tray' });
        }
        toast(auto.checked ? '已开启开机自启' : '已关闭开机自启');
      });
    };

    // 备份与迁移
    m.querySelector('#s-exp-full').onclick = function () { doExport('full'); };
    m.querySelector('#s-exp-lite').onclick = function () { doExport('lite'); };
    m.querySelector('#s-import').onclick = function () {
      call('import-backup', {}).then(function (r) {
        if (!r || r.cancel) return;
        if (r.ok) reloadState().then(function () { toast(r.needRestart ? '已导入，重启后工具数据完整生效' : '已导入'); });
        else toast('导入失败：' + (r.error || ''));
      });
    };
  }

  /* ---------------- 热键/备份辅助 ---------------- */
  function pushHotkey() {
    var hk2 = { enabled: state.settings.hotkey !== false };
    if (state.settings.hotkeyMod != null) hk2.mod = state.settings.hotkeyMod;
    if (state.settings.hotkeyVk != null) hk2.vk = state.settings.hotkeyVk;
    fire('set-hotkey', hk2);
  }
  function evKeyToInfo(e) {
    var mod = 0;
    if (e.ctrlKey) mod |= 0x2;
    if (e.altKey) mod |= 0x1;
    if (e.shiftKey) mod |= 0x4;
    if (e.metaKey) mod |= 0x8;
    var k = e.key || '', vk = 0;
    if (k === ' ' || k === 'Spacebar') vk = 0x20;
    else if (/^F([1-9]|1[0-2])$/.test(k)) vk = 0x70 + (parseInt(k.slice(1), 10) - 1);
    else if (k.length === 1) vk = k.toUpperCase().charCodeAt(0);
    else { var nm = { ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28, Home: 0x24, End: 0x23, PageUp: 0x21, PageDown: 0x22, Insert: 0x2D, Delete: 0x2E }; vk = nm[k] || 0; }
    return { mod: mod, vk: vk };
  }
  function vkName(vk) {
    if (vk >= 0x30 && vk <= 0x39) return String.fromCharCode(vk);
    if (vk >= 0x41 && vk <= 0x5A) return String.fromCharCode(vk);
    if (vk >= 0x70 && vk <= 0x7B) return 'F' + (vk - 0x6F);
    var map = { 0x20: 'Space', 0x24: 'Home', 0x23: 'End', 0x21: 'PgUp', 0x22: 'PgDn', 0x25: '←', 0x26: '↑', 0x27: '→', 0x28: '↓', 0x2D: 'Ins', 0x2E: 'Del' };
    return map[vk] || ('键' + vk);
  }
  function hotkeyLabel(mod, vk) {
    var p = [];
    if (mod & 0x2) p.push('Ctrl');
    if (mod & 0x1) p.push('Alt');
    if (mod & 0x4) p.push('Shift');
    if (mod & 0x8) p.push('Win');
    p.push(vkName(vk));
    return p.join(' + ');
  }
  function fmtSize(n) {
    n = n || 0;
    if (n < 1024) return n + ' B';
    if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1048576).toFixed(1) + ' MB';
  }
  function doExport(level) {
    call('export-backup', { level: level }).then(function (r) {
      if (!r || r.cancel) return;
      if (r.ok) toast('已导出 ' + (r.entries || 0) + ' 项 · ' + fmtSize(r.size));
      else toast('导出失败：' + (r.error || ''));
    });
  }
  function reloadState() {
    return call('get-state').then(function (r) {
      if (!r || !r.stateJson) return;
      try {
        state = JSON.parse(r.stateJson);
        if (!state.apps) state.apps = [];
        if (!state.settings) state.settings = {};
        for (var k in DEFAULT_SETTINGS) if (state.settings[k] == null) state.settings[k] = DEFAULT_SETTINGS[k];
        normalizeOrd(); applySettingsToHost(); render();
      } catch (e) { }
    });
  }

  /* ---------------- 提示 ---------------- */
  function toast(text) {
    var t = document.createElement('div');
    t.className = 'toast';
    t.textContent = text;
    els.toasts.appendChild(t);
    setTimeout(function () { t.classList.add('out'); }, 1800);
    setTimeout(function () { t.remove(); }, 2200);
  }

  /* ---------------- 初始化 ---------------- */
  function wireChrome() {
    els.emptyAdd.addEventListener('click', addDialog);
    els.chips.addEventListener('click', function (e) {
      var b = e.target.closest('.chip-tag');
      if (!b) return;
      filterTag = b.dataset.t || '';
      render();
    });

    window.addEventListener('keydown', function (e) {
      if (e.ctrlKey && (e.key === 'k' || e.key === 'K')) {
        // 搜索框在原生标签栏上，聚焦请求交给宿主
        e.preventDefault();
        fire('focus-search');
        return;
      }
      if (e.key === 'Escape') {
        if (modalOpen()) { closeModal(); return; }
        if (fly.gid) { fly.pinned = false; closeFly(true); return; }   // 展开着的文件夹优先收起
        if (searchQ) { fire('clear-search'); searchQ = ''; render(); }
      }
    });

    // 内容滚动时收起信息提示，避免提示钉在旧位置；展开着的分组跟着卡片走
    els.main = document.getElementById('main');
    els.main.addEventListener('scroll', function () {
      hideTip();
      if (fly.gid && nodes[fly.gid]) positionFly(fly.el, nodes[fly.gid]);
      else closeFly(true);
    });
    window.addEventListener('blur', function () { fly.pinned = false; closeFly(true); hideTip(); });
    // 窗口尺寸变化（拖边框）：只在列数真的变了时补一次滑动面
    window.addEventListener('resize', onViewportResize);
  }

  function mockSeed() {
    if (!hasBridge && !localStorage.getItem('pd-state')) {
      var now = Date.now();
      state.apps = [
        { id: 'd1', name: '时钟演示', kind: 'url', target: 'https://www.bing.com', openMode: 'inplace', tags: ['演示'], pinned: true, createdAt: now - 3000000, lastOpenedAt: now - 1000000, opens: 2 },
        { id: 'd2', name: 'Todos', kind: 'url', target: 'https://todomvc.com', openMode: 'window', tags: ['演示', '效率'], createdAt: now - 2000000, lastOpenedAt: 0, opens: 0 },
        { id: 'd3', name: '本地便签', kind: 'file', target: 'D:\\demo\\便签.html', openMode: 'inplace', createdAt: now - 1000000, lastOpenedAt: 0, opens: 0 }
      ];
      save();
    }
  }

  /* 启动还原：上次钉在桌面的小组件按原位置/尺寸回来（状态持久化） */
  function restoreWidgets() {
    if (!hasBridge) return;
    state.apps.forEach(function (a) {
      if (!a.wp || isGroup(a)) return;
      resolveUrl(a).then(function (url) {
        if (!url) return;
        pinned[a.id] = true;
        call('open-widget', {
          url: url, title: a.name, appId: a.id,
          x: a.wx || 0, y: a.wy || 0,
          width: a.ww || 380, height: a.wh || 280
        });
      });
    });
  }

  function init() {
    els.emptyAdd = document.getElementById('empty-add');
    els.grid = document.getElementById('grid');
    els.chips = document.getElementById('chips');
    els.empty = document.getElementById('empty');
    els.statLeft = document.getElementById('stat-left');
    els.modalRoot = document.getElementById('modal-root');
    els.toasts = document.getElementById('toasts');
    els.cardtip = document.getElementById('cardtip');
    els.main = document.getElementById('main');
    document.getElementById('mockchip').classList.toggle('hidden', hasBridge);

    document.title = '页坞 PageDock';
    wireChrome();
    installColorPickers(document);   // 所有原生取色框改走带「确定/取消」的面板
    applyTheme();          // 先按默认主题着色，加载配置后再校正
    fire('set-dim', { on: false });   // 页面重新加载后，宿主可能残留上一次的边条压暗状态

    mockSeed();
    call('get-state').then(function (r) {
      if (r && r.stateJson) {
        try {
          var s = JSON.parse(r.stateJson);
          if (s && Array.isArray(s.apps)) {
            state.apps = s.apps;
            state.settings = Object.assign({}, DEFAULT_SETTINGS, (s && s.settings) || {});
          }
        } catch (e) { }
      }
      if (r) {
        info.dataDir = r.dataDir || '';
        info.exeDir = r.exeDir || '';
        if (r.version) {
          info.version = r.version;
          document.getElementById('ver').textContent = 'v' + r.version;
        }
      }
      applySettingsToHost(); // 主题 / 关闭行为 / 热键 同步给宿主
      normalizeOrd();        // 首次升级：用旧自动序补齐 ord，之后尊重手动拖拽顺序
      render();
      restoreWidgets(); // 上次钉在桌面的小组件按原位置恢复
    });
  }

  document.addEventListener('DOMContentLoaded', init);
})();
