// Console completion (D105): the list over the command input, the grey preview, the
// usage line and Tab. Everything here runs in the browser on data the server sends
// once per change (GET /console-completion/{instance}); a keystroke never leaves the
// page.
//
// Two halves. createEngine() is pure: it takes the payload and answers "what can
// go here", and tests/js/console-completion.test.js runs it under Node against the
// payload the suite pins. attach() is the page: keys, the list, the preview, history,
// and the send, which goes back to the server so the Send rules stay in C#.
(function (root, factory) {
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    } else {
        root.bedrockSentryCompletion = api;
    }
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const eqi = (a, b) => a.toLowerCase() === b.toLowerCase();

    // Selectors and their arguments are the parser's own and are the same on every
    // version, so they are written here rather than sent.
    const SELECTORS = [
        ['@a', 'all players'], ['@p', 'nearest player'], ['@r', 'a random player'], ['@e', 'all entities'],
        ['@s', 'whoever runs the command'], ['@initiator', 'the player talking to an NPC'],
    ];
    const SEL_KEYS = [
        ['type', 'entity type; ! excludes'], ['name', 'name; ! excludes'], ['tag', 'tag; ! excludes'], ['family', 'family'],
        ['r', 'at most this far'], ['rm', 'at least this far'], ['c', 'how many'], ['m', 'game mode'],
        ['x', 'x of the origin'], ['y', 'y of the origin'], ['z', 'z of the origin'], ['dx', 'box size along x'],
        ['dy', 'box size along y'], ['dz', 'box size along z'], ['l', 'at most this level'], ['lm', 'at least this level'],
        ['rx', 'at most this pitch'], ['rxm', 'at least this pitch'], ['ry', 'at most this yaw'], ['rym', 'at least this yaw'],
        ['scores', '{objective=range}'], ['hasitem', '{item=…}'], ['haspermission', '{camera=enabled}'], ['has_property', '{property=value}'],
    ];

    // Short forms the server also takes, shown beside the full name rather than listed twice.
    const SHORT = {
        Difficulty: { peaceful: 'p or 0', easy: 'e or 1', normal: 'n or 2', hard: 'h or 3' },
        GameMode: { survival: 's or 0', creative: 'c or 1', adventure: 'a or 2', default: 'd or 5' },
    };
    const SHORT_ONLY = { Difficulty: ['p', 'e', 'n', 'h'], GameMode: ['s', 'c', 'a', 'd'] };
    const REST = new Set(['message', 'text', 'json']);
    const RX = {
        int: /^[+-]?\d+$/,
        float: /^[+-]?(\d+\.?\d*|\.\d+)$/,
        coord: /^([~^]([+-]?(\d+\.?\d*|\.\d+))?|[+-]?(\d+\.?\d*|\.\d+))$/,
        range: /^!?([+-]?\d+)?(\.\.)?([+-]?\d+)?$/,
        id: /^[A-Za-z0-9_:.\-]+$/,
    };
    const TEMPLATE = '{"rawtext":[{"text":""}]}';
    const DISPLAY_TYPE = { ExecuteChainedOption_0: 'subcommand' };
    const quoteName = (n) => (/[\s"]/.test(n) ? '"' + n.replace(/"/g, '\\"') + '"' : n);

    function paramText(p) {
        if (p.k === 'lit') return p.t;
        const [o, c] = p.o ? ['[', ']'] : ['<', '>'];
        if (p.k === 'alt') return o + p.v.join('|') + c;
        return o + p.l + ': ' + (DISPLAY_TYPE[p.t] || p.t) + c + (p.s || '');
    }

    function pkind(p) {
        if (p.k === 'lit') return 'lit';
        if (p.k === 'alt') return 'alt';
        if (p.t === 'x y z') return 'xyz';
        if (p.t === 'command') return 'command';
        if (p.t === 'ExecuteChainedOption_0') return 'chain';
        return REST.has(p.t) ? 'rest' : 'one';
    }

    // Words, keeping "quoted text", [selector or block-state brackets] and {json} whole.
    function tokenize(s) {
        const toks = [];
        let i = 0;
        while (i < s.length) {
            while (i < s.length && s[i] === ' ') i++;
            if (i >= s.length) break;
            const start = i;
            let sq = 0, cu = 0, q = false;
            while (i < s.length) {
                const ch = s[i];
                if (q) { if (ch === '\\') { i += 2; continue; } if (ch === '"') q = false; i++; continue; }
                if (ch === '"') q = true;
                else if (ch === '[') sq++;
                else if (ch === ']') sq = Math.max(0, sq - 1);
                else if (ch === '{') cu++;
                else if (ch === '}') cu = Math.max(0, cu - 1);
                else if (ch === ' ' && sq === 0 && cu === 0) break;
                i++;
            }
            toks.push({ text: s.slice(start, Math.min(i, s.length)), start, end: Math.min(i, s.length) });
        }
        return toks;
    }

    // Prefix beats the start of a word, which beats anywhere inside.
    function rank(key, needle) {
        const k = key.toLowerCase(), n = needle.toLowerCase();
        if (!n || k.startsWith(n)) return { r: 0, at: 0 };
        // A namespace is not part of the name: `pla` finds minecraft:plains before
        // minecraft:ice_plains, as it finds plains before ice_plains. Below a match of
        // the whole word, so `hom` still finds the alias home before example:home.
        const colon = k.indexOf(':');
        if (colon > 0 && !n.includes(':') && k.startsWith(n, colon + 1)) return { r: 0.5, at: colon + 1 };
        for (let i = 1; i < k.length; i++) if ('_.:- /'.includes(k[i - 1]) && k.startsWith(n, i)) return { r: 1, at: i };
        const i = k.indexOf(n);
        return i >= 0 ? { r: 2, at: i } : null;
    }

    function cand(insert, kind, desc, needle, extra) {
        return Object.assign({ insert, key: insert, needle, kind, desc: desc || '', space: true }, extra || {});
    }

    function createEngine(data) {
        const D = data;
        const CMDS = D.commands;
        const CMD_NAMES = Object.keys(CMDS).sort((a, b) => (a === '?') - (b === '?') || a.localeCompare(b));
        const ADDON = D.addon || { names: [], loaded: true, read: false, ids: { Block: [], Item: [], EntityType: [] }, functions: [] };
        const summonRefused = new Set(D.summonRefused || []);
        const players = { online: [], known: [], allowlist: [] };

        const enumValues = (t) => (t === 'Boolean' ? ['true', 'false'] : (D.enums[t] || null));
        const lookup = (name) => { const n = name.toLowerCase(); return Object.prototype.hasOwnProperty.call(CMDS, n) ? n : null; };
        const canonical = (name) => { const c = CMDS[name]; return c && c.a ? c.a : name; };
        const addonName = () => (ADDON.names.length === 1 ? ADDON.names[0] : 'the add-ons');

        function isTarget(t) {
            if (/^@(a|e|p|r|s|initiator)(\[.*\])?$/i.test(t)) return true;
            if (t.startsWith('@')) return false;
            if (t.startsWith('"')) return t.length >= 2 && t.endsWith('"');
            // A bare name: a gamertag starts with a letter, so ~, ^ and numbers stay coordinates.
            return /^[A-Za-z][\w.\-]*$/.test(t);
        }

        function accepts(p, tok) {
            let text = tok;
            if (p.s) {
                if (!text.toUpperCase().endsWith(p.s.toUpperCase())) return false;
                text = text.slice(0, -p.s.length);
            }
            switch (p.t) {
                case 'int': return RX.int.test(text);
                case 'float': return RX.float.test(text);
                case 'wildcard int': return text === '*' || RX.int.test(text);
                case 'integer range': return RX.range.test(text) && /\d/.test(text);
                case 'value': return RX.coord.test(text);
                case 'target': return isTarget(text);
                case 'CommandName': return !!lookup(text);
                case 'Block': case 'Item': case 'EntityType': case 'Biome': case 'Effect': case 'Enchant':
                case 'features': case 'featureRules': return RX.id.test(text);
                case 'string': case 'filepath': case 'block states': case 'ToolOrHand': return text.length > 0;
                default: {
                    const vals = enumValues(p.t);
                    return vals ? vals.some((v) => eqi(v, text)) : text.length > 0;
                }
            }
        }

        // Every way the typed words can be read, ending where the next word would go.
        function walkLine(toks, ti, out, depth) {
            if (depth > 8) return;
            if (ti === toks.length) { out.push({ kind: 'command', depth }); return; }
            const raw = toks[ti].text;
            const name = lookup(raw.startsWith('/') ? raw.slice(1) : raw);
            if (!name) { out.push({ kind: 'unknown', name: raw, depth }); return; }
            for (const ov of CMDS[name].o) matchParams(name, ov, 0, toks, ti + 1, out, depth);
        }

        const isChainForm = (ov) => ov.length > 0 && (ov[0].k === 'lit' || (ov[0].k === 'p' && ov[0].t === 'Option_If_Unless'));

        function walkChain(toks, ti, out, depth) {
            if (!CMDS.execute) return;
            for (const ov of CMDS.execute.o) if (isChainForm(ov)) matchParams('execute', ov, 0, toks, ti, out, depth + 1);
        }

        function matchParams(cmd, ov, pi, toks, ti, out, depth) {
            if (depth > 12) return;
            if (pi === ov.length) { if (ti === toks.length) out.push({ kind: 'end', cmd, ov, pi, depth }); return; }
            const p = ov[pi], k = pkind(p);
            if (ti === toks.length) {
                if (k === 'command') { out.push({ kind: 'command', depth: depth + 1, nested: true }); return; }
                if (k === 'chain') { walkChain(toks, ti, out, depth); return; }
                out.push({ kind: 'param', cmd, ov, pi, coord: 0, style: null, depth });
                return;
            }
            const t = toks[ti].text;
            switch (k) {
                case 'lit': if (eqi(t, p.t)) matchParams(cmd, ov, pi + 1, toks, ti + 1, out, depth); return;
                case 'alt': if (p.v.some((v) => eqi(v, t))) matchParams(cmd, ov, pi + 1, toks, ti + 1, out, depth); return;
                case 'xyz': {
                    let style = null;
                    for (let c = 0; c < 3; c++) {
                        if (ti + c === toks.length) { out.push({ kind: 'param', cmd, ov, pi, coord: c, style, depth }); return; }
                        const s = toks[ti + c].text;
                        if (!RX.coord.test(s)) return;
                        const st = s[0] === '^' ? 'local' : 'world';
                        if (style && st !== style) return; // the server refuses ^ mixed with ~ or a number
                        style = style || st;
                    }
                    matchParams(cmd, ov, pi + 1, toks, ti + 3, out, depth);
                    return;
                }
                case 'rest': out.push({ kind: 'rest', cmd, ov, pi, depth }); return;
                case 'command': walkLine(toks, ti, out, depth + 1); return;
                case 'chain': walkChain(toks, ti, out, depth); return;
                default: if (accepts(p, t)) matchParams(cmd, ov, pi + 1, toks, ti + 1, out, depth);
            }
        }

        function context(value, caret) {
            const at = caret == null ? value.length : caret;
            const pre = value.slice(0, at);
            const toks = tokenize(pre);
            let partial = '', pstart = at;
            const last = toks[toks.length - 1];
            if (last && last.end === pre.length) { partial = last.text; pstart = last.start; toks.pop(); }
            let pend = at;
            while (pend < value.length && value[pend] !== ' ') pend++;
            const frontiers = [];
            walkLine(toks, 0, frontiers, 0);
            return { value, caret: at, toks, partial, pstart, pend, frontiers };
        }

        // The server types the allowlist's name as a plain string, so it is completed from
        // what BedrockSentry knows: players seen who are not on the list for add, the list
        // for remove.
        function allowlistCandidates(action, partial) {
            const out = [];
            const needle = partial.startsWith('"') ? partial.slice(1) : partial;
            const list = players.allowlist || [];
            const listed = new Set(list.map((n) => n.toLowerCase()));
            if (action === 'remove') {
                for (const n of list) out.push(cand(quoteName(n), 'on the allowlist', '', needle, { key: n }));
            } else if (action === 'add') {
                const seen = new Set();
                for (const n of [...players.online, ...players.known]) {
                    const k = n.toLowerCase();
                    if (listed.has(k) || seen.has(k)) continue;
                    seen.add(k);
                    out.push(cand(quoteName(n), players.online.includes(n) ? 'online player' : 'seen before', 'not on the allowlist', needle, { key: n }));
                }
            }
            return out;
        }

        // A name with a namespace is whole; a vanilla name takes minecraft: only when typed.
        function registryCandidates(reg, kind, partial, out, skip) {
            const ns = /^minecraft:/i.test(partial);
            const bareNeedle = ns ? partial.slice(10) : partial;
            for (const v of D.registries[reg] || []) {
                if (skip && skip.has(v)) continue;
                if (v.includes(':')) out.push(cand(v, kind, '', partial));
                else out.push(cand((ns ? 'minecraft:' : '') + v, kind, '', bareNeedle, { key: v }));
            }
            for (const v of (ADDON.ids && ADDON.ids[reg]) || []) out.push(cand(v, 'add-on ' + kind, 'from ' + addonName(), partial));
            return out;
        }

        function coordCandidates(f, partial) {
            const out = [];
            if (f.coord === 0) {
                out.push(cand('~ ~ ~', 'position', 'where the command runs', partial));
                out.push(cand('^ ^ ^', 'position', 'from where the runner faces', partial));
            } else if (f.style === 'local') {
                out.push(cand(f.coord === 1 ? '^ ^' : '^', 'position', '^ cannot mix with ~ or numbers', partial));
            } else {
                out.push(cand(f.coord === 1 ? '~ ~' : '~', 'position', 'relative', partial));
            }
            return out;
        }

        function entityIds() {
            return (D.registries.EntityType || []).concat((ADDON.ids && ADDON.ids.EntityType) || []);
        }

        function selectorArgCandidates(inner, partial) {
            const seg = inner.slice(inner.lastIndexOf(',') + 1).replace(/^\s+/, '');
            const head = partial.slice(0, partial.length - seg.length);
            const out = [];
            const eq = seg.indexOf('=');
            if (eq < 0) {
                for (const [k, d] of SEL_KEYS) out.push(cand(head + k + '=', 'selector argument', d, seg, { key: k + '=', space: false }));
                return out;
            }
            const key = seg.slice(0, eq).trim().toLowerCase();
            let val = seg.slice(eq + 1);
            const neg = val.startsWith('!');
            if (neg) val = val.slice(1);
            const vhead = head + seg.slice(0, eq + 1) + (neg ? '!' : '');
            let vals = [];
            if (key === 'type') vals = entityIds().map((v) => [v, v, v.includes(':') ? 'add-on entity' : 'entity']);
            else if (key === 'm') vals = ['survival', 'creative', 'adventure', 'spectator'].map((v) => [v, v, 'game mode']);
            else if (key === 'name') vals = players.online.map((n) => [quoteName(n), n, 'online player']);
            for (const [ins, key2, kind] of vals) out.push(cand(vhead + ins, kind, '', val.replace(/^"/, ''), { key: key2, space: false }));
            return out;
        }

        function targetCandidates(partial) {
            const m = /^(@[a-z]+)\[(.*)$/i.exec(partial);
            if (m && !partial.endsWith(']')) return selectorArgCandidates(m[2], partial);
            const out = [];
            const needle = partial.startsWith('"') ? partial.slice(1) : partial;
            for (const n of players.online) out.push(cand(quoteName(n), 'online player', '', needle, { key: n }));
            for (const [v, d] of SELECTORS) out.push(cand(v, 'selector', d, partial, { space: false }));
            return out;
        }

        function paramCandidates(f, partial, ctx) {
            const p = f.ov[f.pi], k = pkind(p), lbl = (p.l || '').toLowerCase();
            const out = [];
            if (k === 'lit') { out.push(cand(p.t, 'option', '', partial)); return out; }
            if (k === 'alt') { p.v.forEach((v) => out.push(cand(v, 'option', '', partial))); return out; }
            if (k === 'xyz') return coordCandidates(f, partial);
            if (k === 'rest') {
                if (p.t === 'json') out.push(cand(TEMPLATE, 'JSON text', 'a template; type inside the quotes', partial, { space: false, caretAt: TEMPLATE.indexOf('""') + 1 }));
                return out;
            }
            switch (p.t) {
                case 'target': return targetCandidates(partial);
                case 'Boolean': ['true', 'false'].forEach((v) => out.push(cand(v, lbl || 'value', '', partial))); return out;
                case 'value': out.push(cand('~', 'relative', 'unchanged', partial)); return out;
                case 'CommandName': CMD_NAMES.forEach((n) => out.push(cand(n, 'command', CMDS[n].d, partial))); return out;
                case 'ToolOrHand':
                    out.push(cand('mainhand', 'hand', 'the item in the main hand', partial), cand('offhand', 'hand', 'the item in the off hand', partial));
                    return registryCandidates('Item', 'item', partial, out);
                case 'Block': return registryCandidates('Block', 'block', partial, out);
                case 'Item': return registryCandidates('Item', 'item', partial, out);
                case 'EntityType': return registryCandidates('EntityType', 'entity', partial, out, canonical(f.cmd) === 'summon' ? summonRefused : null);
                case 'Biome': return registryCandidates('Biome', 'biome', partial, out);
                case 'Effect': return registryCandidates('Effect', 'effect', partial, out);
                case 'Enchant': return registryCandidates('Enchant', 'enchantment', partial, out);
                case 'filepath':
                    if (canonical(f.cmd) === 'function') (ADDON.functions || []).forEach((v) => out.push(cand(v, 'function', 'from ' + addonName(), partial)));
                    return out;
                case 'string':
                    if (canonical(f.cmd) === 'allowlist' && lbl === 'name') {
                        const action = ctx && ctx.toks.length ? ctx.toks[ctx.toks.length - 1].text.toLowerCase() : '';
                        return allowlistCandidates(action, partial);
                    }
                    if (canonical(f.cmd) === 'camera' && lbl === 'preset') (D.cameraPresets || []).forEach((v) => out.push(cand(v, 'camera preset', '', partial)));
                    if (canonical(f.cmd) === 'locate' && (lbl === 'structure' || lbl === 'feature')) (D.structures || []).forEach((v) => out.push(cand(v, 'structure', '', partial)));
                    return out;
                default: {
                    const vals = enumValues(p.t);
                    if (vals) {
                        const shortOnly = SHORT_ONLY[p.t] || [];
                        vals.filter((v) => !shortOnly.includes(v)).forEach((v) => {
                            const s = SHORT[p.t] && SHORT[p.t][v];
                            out.push(cand(v, lbl || 'value', s ? 'or ' + s : '', partial));
                        });
                    }
                    return out;
                }
            }
        }

        function items(ctx) {
            const seen = new Set();
            const raw = [];
            const add = (c) => { const key = c.insert.toLowerCase(); if (!seen.has(key)) { seen.add(key); raw.push(c); } };
            for (const f of ctx.frontiers) {
                if (f.kind === 'command') {
                    const slash = !f.nested && ctx.partial.startsWith('/');
                    const needle = slash ? ctx.partial.slice(1) : ctx.partial;
                    for (const n of CMD_NAMES) {
                        const c = CMDS[n];
                        const kind = c.a ? 'alias of ' + c.a : (c.addon ? 'add-on command' : 'command');
                        add(cand((slash ? '/' : '') + n, kind, c.d, needle, { key: n }));
                    }
                } else if (f.kind === 'param') {
                    paramCandidates(f, ctx.partial, ctx).forEach(add);
                }
            }
            const scored = [];
            raw.forEach((c, idx) => { const s = rank(c.key, c.needle); if (s) scored.push({ c, s, idx }); });
            scored.sort((a, b) => a.s.r - b.s.r || a.idx - b.idx);
            return scored.map((x) => Object.assign({}, x.c, { r: x.s.r, at: x.s.at, len: x.c.needle.length }));
        }

        function formHtml(f) {
            const ov = f.ov, cur = f.kind === 'end' ? -1 : f.pi;
            const start = Math.max(0, f.pi - 2);
            const parts = [`<span class="cc-name">${esc(f.cmd)}</span>`];
            if (start > 0) parts.push('…');
            for (let i = start; i < ov.length; i++) {
                const t = esc(paramText(ov[i]));
                parts.push(i === cur ? `<mark>${t}</mark>` : t);
            }
            return parts.join(' ');
        }

        // What is under the input: warnings, the command and its description, the forms still
        // possible with the next word marked, and whether Enter would send something whole.
        function usage(ctx) {
            const rows = [];
            const first = ctx.toks[0] ? ctx.toks[0].text : ctx.partial;
            if (first.startsWith('/')) rows.push({ cls: 'cc-warn', html: 'The console does not take a leading <code>/</code>. The server answers “Unknown command: /”.' });
            const unknown = ctx.frontiers.find((f) => f.kind === 'unknown');
            if (unknown) rows.push({ cls: 'cc-warn', html: `The server will answer “Unknown command: ${esc(unknown.name.replace(/^\//, ''))}”.` });
            if (!ctx.frontiers.length && ctx.toks.length) {
                const name = lookup(ctx.toks[0].text.replace(/^\//, '')) || ctx.toks[0].text;
                rows.push({ cls: 'cc-warn', html: `No form of <code>${esc(name)}</code> matches what is typed, so the server will answer with a syntax error.` });
            }
            const forms = ctx.frontiers.filter((f) => f.kind === 'param' || f.kind === 'rest' || f.kind === 'end');
            if (forms.length) {
                const name = forms[forms.length - 1].cmd, c = CMDS[name];
                const shown = c && c.a ? `${name} (${c.a})` : name;
                rows.push({ cls: 'cc-desc-line', html: `<span class="cc-name">${esc(shown)}</span> · ${esc(c ? c.d : '')}` });
                if (c && c.addon && !ADDON.loaded) {
                    rows.push({ cls: 'cc-warn', html: `${esc(addonName())} loads at the server's next restart. Until then the server answers “Unknown command”.` });
                }
            }
            const htmls = [...new Set(forms.map(formHtml))];
            htmls.slice(0, 3).forEach((h) => rows.push({ cls: 'cc-form', html: h }));
            if (htmls.length > 3) rows.push({ cls: 'cc-more', html: `and ${htmls.length - 3} more form${htmls.length - 3 === 1 ? '' : 's'}` });
            if (forms.length && forms.every((f) => f.kind === 'end' || (f.kind === 'param' && f.ov.slice(f.pi).every((p) => p.o)))
                && ctx.partial === '' && ctx.toks.length) {
                rows.push({ cls: 'cc-ok', html: 'Complete: Enter sends it.' });
            }
            return rows;
        }

        function hint(ctx) {
            const f = ctx.frontiers.find((x) => x.kind === 'param');
            if (!f) return '';
            const p = f.ov[f.pi];
            if (pkind(p) === 'xyz') return ['x y z', 'y z', 'z'][f.coord];
            return paramText(p);
        }

        // The grey preview: the rest of the highlighted match after the cursor, or, where
        // nothing completes, what the server expects next.
        function ghost(ctx, top) {
            if (ctx.caret !== ctx.value.length) return '';
            if (top && ctx.partial && top.insert.toLowerCase().startsWith(ctx.partial.toLowerCase())) return top.insert.slice(ctx.partial.length);
            if (top && !ctx.partial && ctx.value) return top.insert;
            if (!top && !ctx.partial && ctx.value) return hint(ctx);
            return '';
        }

        function nothing(ctx) {
            const f = ctx.frontiers.find((x) => x.kind === 'param' || x.kind === 'rest');
            const what = f ? paramText(f.ov[f.pi]) : null;
            return what ? `Nothing to complete here: <code>${esc(what)}</code> is yours to type.` : 'Nothing to complete here.';
        }

        // What Tab does to the input: the value and where the caret goes.
        function accept(ctx, c) {
            const before = ctx.value.slice(0, ctx.pstart), after = ctx.value.slice(ctx.pend);
            const sp = c.space && after[0] !== ' ' ? ' ' : '';
            const value = before + c.insert + sp + after;
            const caret = c.caretAt != null ? before.length + c.caretAt : before.length + c.insert.length + sp.length;
            return { value, caret };
        }

        return {
            setPlayers(p) {
                players.online = (p && p.online) || [];
                players.known = (p && p.known) || [];
                players.allowlist = p && p.allowlist !== undefined ? p.allowlist : [];
            },
            context, items, usage, hint, ghost, nothing, accept, lookup, canonical,
            commandCount: () => Object.keys(CMDS).filter((n) => !CMDS[n].a).length,
            tag: D.tag,
        };
    }

    // ------------------------------------------------------------------ the page

    const LIMIT = 100;
    const consoles = new Map();

    function highlight(c) {
        const off = c.insert.toLowerCase().indexOf(c.key.toLowerCase());
        if (off < 0 || !c.len) return esc(c.insert);
        const a = off + c.at, b = a + c.len;
        return esc(c.insert.slice(0, a)) + '<b>' + esc(c.insert.slice(a, b)) + '</b>' + esc(c.insert.slice(b));
    }

    // Wires one console's input. `ids` names the input, the list, the usage line, the preview
    // and the Send button; `url` is the instance's completion data; `dotnet` sends.
    function attach(ids, url, dotnet) {
        dispose(ids.input);

        const $ = (id) => document.getElementById(id);
        const input = $(ids.input), list = $(ids.list), usageBox = $(ids.usage);
        const ghostBox = $(ids.ghost), ghostTyped = $(ids.ghostTyped), ghostRest = $(ids.ghostRest), sendButton = $(ids.send);
        if (!input || !list || !usageBox) return;

        const state = {
            engine: null, players: null, loading: null,
            history: [], hi: -1, open: false, items: [], active: 0, ctx: null, escaped: false,
        };

        async function load() {
            try {
                const response = await fetch(url, { cache: 'no-cache', credentials: 'same-origin' });
                if (!response.ok) return;
                const engine = createEngine(await response.json());
                if (state.players) engine.setPlayers(state.players);
                state.engine = engine;
                if (document.activeElement === input) refresh(state.open ? 'keep' : 'close');
            } catch (e) {
                // A page that cannot fetch its data still sends commands as typed.
            }
        }

        function openList(items, active) {
            state.open = true; state.items = items;
            state.active = Math.max(0, Math.min(active, items.length - 1));
            const shown = items.slice(0, LIMIT);
            const count = items.length > LIMIT
                ? `<span>${LIMIT} of ${items.length.toLocaleString()} shown; keep typing to narrow</span>`
                : `<span>${items.length} match${items.length === 1 ? '' : 'es'}</span>`;
            list.innerHTML = shown.map((c, i) =>
                `<div class="cc-item${i === state.active ? ' is-active' : ''}" role="option" id="${ids.list}-opt-${i}" aria-selected="${i === state.active}" data-i="${i}">` +
                `<span class="cc-val">${highlight(c)}</span><span class="cc-desc">${esc(c.desc)}</span><span class="cc-kind">${esc(c.kind)}</span></div>`).join('') +
                `<div class="cc-foot">${count}<span><kbd>Tab</kbd> take · <kbd>↑</kbd><kbd>↓</kbd> choose · <kbd>Esc</kbd> close</span></div>`;
            list.hidden = false;
            input.setAttribute('aria-expanded', 'true');
            input.setAttribute('aria-activedescendant', `${ids.list}-opt-${state.active}`);
            scrollActive();
        }

        function closeList() {
            state.open = false;
            list.hidden = true; list.innerHTML = '';
            input.setAttribute('aria-expanded', 'false');
            input.removeAttribute('aria-activedescendant');
        }

        function scrollActive() {
            const el = document.getElementById(`${ids.list}-opt-${state.active}`);
            if (el) el.scrollIntoView({ block: 'nearest' });
        }

        function move(d) {
            const n = Math.min(state.items.length, LIMIT);
            if (!n) return;
            const prev = document.getElementById(`${ids.list}-opt-${state.active}`);
            if (prev) { prev.classList.remove('is-active'); prev.setAttribute('aria-selected', 'false'); }
            state.active = (state.active + d + n) % n;
            const el = document.getElementById(`${ids.list}-opt-${state.active}`);
            if (el) { el.classList.add('is-active'); el.setAttribute('aria-selected', 'true'); }
            input.setAttribute('aria-activedescendant', `${ids.list}-opt-${state.active}`);
            scrollActive();
            showGhost(state.ctx, state.items[state.active]);
        }

        function showGhost(ctx, top) {
            if (!ghostBox) return;
            const rest = ctx && state.engine && !input.disabled ? state.engine.ghost(ctx, top) : '';
            ghostTyped.textContent = ctx ? ctx.value : '';
            ghostRest.textContent = rest;
            ghostBox.hidden = !rest;
            ghostBox.scrollLeft = input.scrollLeft;
        }

        function renderUsage(rows) {
            usageBox.innerHTML = rows.map((r) => `<div class="${r.cls}">${r.html}</div>`).join('');
        }

        function refresh(open) {
            const engine = state.engine;
            if (!engine || input.disabled) { closeList(); renderUsage([]); showGhost(null, null); return; }
            const ctx = engine.context(input.value, input.selectionStart);
            state.ctx = ctx;
            const items = engine.items(ctx);
            state.items = items;
            renderUsage(engine.usage(ctx));
            const trivial = items.length === 1 && eqi(items[0].insert, ctx.partial);
            if (open === 'open' && items.length && !trivial) openList(items, 0);
            else if (open === 'keep' && state.open && items.length && !trivial) openList(items, state.active);
            else closeList();
            showGhost(ctx, items.length ? items[state.open ? state.active : 0] : null);
        }

        function acceptItem(c) {
            const next = state.engine.accept(state.engine.context(input.value, input.selectionStart), c);
            input.value = next.value;
            input.setSelectionRange(next.caret, next.caret);
            refresh('open');
        }

        function tab(shift) {
            if (shift) { if (state.open) move(-1); return; }
            if (!state.engine) return;
            const ctx = state.engine.context(input.value, input.selectionStart);
            const items = state.engine.items(ctx);
            if (!items.length) {
                closeList();
                renderUsage([{ cls: 'cc-more', html: state.engine.nothing(ctx) }, ...state.engine.usage(ctx)]);
                return;
            }
            acceptItem(state.open && state.items[state.active] ? state.items[state.active] : items[0]);
        }

        function historyStep(d) {
            const h = state.history;
            if (!h.length) return;
            if (d < 0) state.hi = state.hi < 0 ? h.length - 1 : Math.max(0, state.hi - 1);
            else {
                if (state.hi < 0) return;
                state.hi++;
                if (state.hi >= h.length) { state.hi = -1; input.value = ''; refresh('close'); return; }
            }
            input.value = h[state.hi];
            input.setSelectionRange(input.value.length, input.value.length);
            refresh('close');
        }

        // Exactly what is typed goes; a suggestion is taken with Tab, never with Enter.
        function send() {
            const text = input.value.trim();
            if (!text || input.disabled) return;
            // Cleared before the send: a slow one would leave the command looking unsent.
            input.value = '';
            state.hi = -1;
            if (!state.history.length || state.history[state.history.length - 1] !== text) state.history.push(text);
            refresh('close');
            dotnet.invokeMethodAsync('SendFromBrowser', text).catch(() => { });
            input.focus();
        }

        const onKeyDown = (e) => {
            if (e.key === 'Tab') {
                // Esc then Tab leaves the input, so the keyboard is never trapped in it.
                if (state.escaped) { state.escaped = false; return; }
                e.preventDefault();
                tab(e.shiftKey);
                return;
            }
            if (e.key === 'Shift') return;
            state.escaped = false;
            if (e.key === 'Escape') {
                e.preventDefault();
                closeList(); state.escaped = true;
                showGhost(state.ctx, null);
                return;
            }
            if (e.key === 'Enter') { e.preventDefault(); send(); return; }
            if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
                e.preventDefault();
                if (state.open && state.items.length) move(e.key === 'ArrowUp' ? -1 : 1);
                else historyStep(e.key === 'ArrowUp' ? -1 : 1);
            }
        };
        const onInput = () => { state.hi = -1; refresh(input.value.trim() !== '' ? 'open' : 'close'); };
        const caretMoved = () => refresh(state.open ? 'keep' : 'close');
        const onKeyUp = (e) => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) caretMoved(); };
        const onScroll = () => { if (ghostBox) ghostBox.scrollLeft = input.scrollLeft; };
        const onBlur = () => { setTimeout(() => { if (document.activeElement !== input) { closeList(); if (ghostBox) ghostBox.hidden = true; } }, 120); };
        const onListDown = (e) => {
            e.preventDefault();
            const el = e.target.closest('.cc-item');
            if (!el) return;
            const c = state.items[+el.dataset.i];
            if (c) acceptItem(c);
            input.focus();
        };
        const onSendClick = (e) => { e.preventDefault(); send(); };

        input.addEventListener('keydown', onKeyDown);
        input.addEventListener('input', onInput);
        input.addEventListener('click', caretMoved);
        input.addEventListener('keyup', onKeyUp);
        input.addEventListener('scroll', onScroll);
        input.addEventListener('blur', onBlur);
        list.addEventListener('mousedown', onListDown);
        if (sendButton) sendButton.addEventListener('click', onSendClick);

        consoles.set(ids.input, {
            state, load,
            refresh,
            detach() {
                input.removeEventListener('keydown', onKeyDown);
                input.removeEventListener('input', onInput);
                input.removeEventListener('click', caretMoved);
                input.removeEventListener('keyup', onKeyUp);
                input.removeEventListener('scroll', onScroll);
                input.removeEventListener('blur', onBlur);
                list.removeEventListener('mousedown', onListDown);
                if (sendButton) sendButton.removeEventListener('click', onSendClick);
            },
        });

        load();
    }

    // The players completion offers, sent by the server: who is online, everyone it has
    // seen, and this server's allowlist (null when it could not be read). `tag` is the
    // data's version, and a different one fetches the data again.
    function update(inputId, players, tag) {
        const c = consoles.get(inputId);
        if (!c) return;
        c.state.players = players;
        if (c.state.engine) c.state.engine.setPlayers(players);
        if (tag && c.state.engine && c.state.engine.tag !== tag) c.load();
    }

    function dispose(inputId) {
        const c = consoles.get(inputId);
        if (c) { c.detach(); consoles.delete(inputId); }
    }

    return { createEngine, tokenize, rank, paramText, attach, update, dispose };
});
