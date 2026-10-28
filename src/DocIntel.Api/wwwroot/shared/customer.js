(() => {
    'use strict';
    if (globalThis.DocIntelUX) return;

    const preferenceKey = 'docintel.ui.preferences.v1';
    const themes = ['system', 'light', 'dark'];
    const densities = ['comfortable', 'compact'];
    const commands = new Map();
    const running = new Set();
    const notices = [];
    let preferences = { theme: 'system', density: 'comfortable' };
    let preferenceWarning = '';
    let mounted = false;
    let composing = false;
    let palette;
    let appearance;
    let help;
    let search;
    let results;
    let resultStatus;
    let themeSelect;
    let densitySelect;
    let notifications;
    let visibleCommands = [];
    let activeIndex = 0;

    function validPreferences(value) {
        return value && !Array.isArray(value) && value.version === 1 &&
            themes.includes(value.theme) && densities.includes(value.density) &&
            Object.keys(value).every(key => ['version', 'theme', 'density'].includes(key));
    }

    function applyPreferences() {
        document.documentElement.setAttribute('data-di-theme', preferences.theme);
        document.documentElement.setAttribute('data-di-density', preferences.density);
        if (typeof CustomEvent === 'function') {
            document.dispatchEvent(new CustomEvent('docintel:preferences-changed', {
                detail: Object.freeze({ ...preferences })
            }));
        }
    }

    try {
        const stored = globalThis.localStorage.getItem(preferenceKey);
        if (stored !== null) {
            const parsed = JSON.parse(stored);
            if (!validPreferences(parsed)) throw new TypeError('Invalid visual preferences.');
            preferences = { theme: parsed.theme, density: parsed.density };
        }
    } catch {
        preferenceWarning = 'Appearance preferences could not be read. System theme and comfortable density are in use. ' +
            'You can choose appearance again; browser storage may be unavailable.';
    }
    applyPreferences();

    function element(tag, text, className) {
        const node = document.createElement(tag);
        if (text !== undefined) node.textContent = text;
        if (className) node.className = className;
        return node;
    }

    function button(text, run, primary = false) {
        const node = element('button', text, `di-button${primary ? ' di-button--primary' : ''}`);
        node.type = 'button';
        node.addEventListener('click', run);
        return node;
    }

    function safeReference(value) {
        return typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,95}$/.test(value) ? value : '';
    }

    function inform(message, options = {}) {
        const kind = ['info', 'success', 'warning', 'error'].includes(options.kind) ? options.kind : 'info';
        const notice = {
            message: typeof message === 'string' && message.trim() ? message : 'The operation could not be completed.',
            kind, reference: safeReference(options.reference),
            persistent: kind !== 'success' || options.persistent === true,
            dismissed: false, node: null, timer: null
        };
        notices.push(notice);
        if (mounted) renderNotice(notice);
        return () => dismissNotice(notice);
    }

    function dismissNotice(notice) {
        notice.dismissed = true;
        clearTimeout(notice.timer);
        notice.node?.remove();
        const index = notices.indexOf(notice);
        if (index >= 0) notices.splice(index, 1);
    }

    function renderNotice(notice) {
        if (notice.dismissed || notice.node) return;
        const node = element('div', undefined, 'di-notice');
        node.setAttribute('data-kind', notice.kind);
        node.setAttribute('role', notice.kind === 'error' ? 'alert' : 'status');
        node.setAttribute('aria-live', notice.kind === 'error' ? 'assertive' : 'polite');
        node.setAttribute('aria-atomic', 'true');
        const text = element('p', notice.message);
        node.appendChild(text);
        if (notice.reference) node.appendChild(element('p', `Reference: ${notice.reference}`, 'di-muted'));
        const dismiss = button('Dismiss', () => dismissNotice(notice));
        dismiss.setAttribute('aria-label', 'Dismiss notification');
        node.appendChild(dismiss);
        notifications.appendChild(node);
        notice.node = node;
        // Successful acknowledgements may fade; errors and operational statuses require dismissal.
        if (!notice.persistent) {
            const schedule = () => {
                clearTimeout(notice.timer);
                notice.timer = setTimeout(() => {
                    if (!node.contains(document.activeElement)) dismissNotice(notice);
                }, 6000);
            };
            node.addEventListener('mouseenter', () => clearTimeout(notice.timer));
            node.addEventListener('focusin', () => clearTimeout(notice.timer));
            node.addEventListener('mouseleave', schedule);
            node.addEventListener('focusout', schedule);
            schedule();
        }
    }

    function setPreferences(patch) {
        if (!patch || Array.isArray(patch) || typeof patch !== 'object' ||
            Object.keys(patch).some(key => !['theme', 'density'].includes(key)) ||
            !validPreferences({ ...preferences, ...patch, version: 1 })) {
            inform('Appearance was not changed. Choose a supported theme and density.', { kind: 'error' });
            return false;
        }
        preferences = { ...preferences, ...patch };
        applyPreferences();
        try {
            globalThis.localStorage.setItem(preferenceKey, JSON.stringify({ version: 1, ...preferences }));
            return true;
        } catch {
            inform('Appearance changed for this page only. Your browser blocked saving preferences; ' +
                'they may reset when you leave this page.', { kind: 'warning' });
            return false;
        }
    }

    function enabled(command) {
        try { return !running.has(command.id) && (!command.enabled || command.enabled() === true); }
        catch { return false; }
    }

    // Screens own authorization and confirmation. Re-registering an ID replaces its current handler.
    function registerCommands(batch) {
        if (!Array.isArray(batch)) throw new TypeError('Commands must be an array.');
        const entries = batch.map(command => {
            if (!command || typeof command.id !== 'string' || !/^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$/.test(command.id) ||
                command.id.startsWith('docintel.') || typeof command.title !== 'string' || !command.title.trim() ||
                typeof command.run !== 'function' ||
                (command.enabled !== undefined && typeof command.enabled !== 'function') ||
                (command.keywords !== undefined && typeof command.keywords !== 'string' &&
                    (!Array.isArray(command.keywords) || !command.keywords.every(word => typeof word === 'string')))) {
                throw new TypeError('A command needs a unique ID, a title, a run function and optional keywords/enabled.');
            }
            return Object.freeze({ ...command, keywords: Array.isArray(command.keywords)
                ? command.keywords.join(' ') : command.keywords || '' });
        });
        for (const command of entries) commands.set(command.id, command);
        if (palette?.open) renderCommands();
        return () => {
            for (const command of entries) {
                if (commands.get(command.id) === command) commands.delete(command.id);
            }
            if (palette?.open) renderCommands();
        };
    }

    async function execute(id) {
        const command = commands.get(id);
        if (!command || !enabled(command)) {
            renderCommands();
            palette.close();
            inform('That command is no longer available. Check the current page and your access.', { kind: 'warning' });
            return;
        }
        running.add(id);
        palette.close();
        try { await command.run(); }
        catch {
            inform('This command could not be completed. Check the page status before trying again. ' +
                'No automatic retry was made.', { kind: 'error' });
        } finally { running.delete(id); }
    }

    function updateActive() {
        Array.from(results.children).forEach((node, index) => {
            node.setAttribute('aria-selected', String(index === activeIndex));
            if (index === activeIndex) {
                search.setAttribute('aria-activedescendant', node.id);
                node.scrollIntoView?.({ block: 'nearest' });
            }
        });
        if (!visibleCommands.length) search.removeAttribute('aria-activedescendant');
    }

    function renderCommands() {
        const words = search.value.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
        visibleCommands = Array.from(commands.values()).filter(command => {
            if (!enabled(command)) return false;
            const haystack = `${command.title} ${command.keywords || ''}`.toLocaleLowerCase();
            return words.every(word => haystack.includes(word));
        });
        results.replaceChildren();
        activeIndex = 0;
        visibleCommands.forEach((command, index) => {
            const option = element('div', command.title, 'di-command-option');
            option.id = `di-command-option-${index}`;
            option.setAttribute('role', 'option');
            option.addEventListener('click', () => { void execute(command.id); });
            results.appendChild(option);
        });
        resultStatus.textContent = visibleCommands.length
            ? `${visibleCommands.length} available command${visibleCommands.length === 1 ? '' : 's'}.`
            : 'No available commands match. Try a different search.';
        updateActive();
    }

    function createDialog(id, title) {
        const dialog = element('dialog', undefined, 'di-dialog');
        dialog.id = id;
        dialog.setAttribute('aria-labelledby', `${id}-title`);
        const header = element('div', undefined, 'di-dialog-header');
        const heading = element('h2', title);
        heading.id = `${id}-title`;
        header.appendChild(heading);
        header.appendChild(button('Close', () => dialog.close()));
        dialog.appendChild(header);
        dialog.addEventListener('cancel', event => { event.preventDefault(); dialog.close(); });
        dialog.addEventListener('close', () => {
            if (dialog === palette) search.setAttribute('aria-expanded', 'false');
            const previous = dialog.diReturnFocus;
            // Opening another native dialog must not send focus behind its modal boundary.
            if (!document.querySelector('dialog[open]') && previous?.isConnected) previous.focus();
        });
        document.body.appendChild(dialog);
        return dialog;
    }

    function showDialog(dialog, focus) {
        if (!mounted || !dialog) return false;
        if (dialog.open) { focus.focus(); return true; }
        if (document.querySelector('dialog[open]')) return false;
        if (typeof dialog.showModal !== 'function') {
            inform('This browser cannot open the accessible dialog. Use a current browser to change appearance or search commands.',
                { kind: 'warning' });
            return false;
        }
        dialog.diReturnFocus = document.activeElement;
        dialog.showModal();
        focus.focus();
        return true;
    }

    function openCommands() {
        if (!mounted) return false;
        search.value = '';
        renderCommands();
        const opened = showDialog(palette, search);
        search.setAttribute('aria-expanded', String(opened));
        return opened;
    }

    function openPreferences() {
        if (!mounted) return false;
        themeSelect.value = preferences.theme;
        densitySelect.value = preferences.density;
        return showDialog(appearance, themeSelect);
    }

    function openShortcutHelp() {
        if (!mounted) return false;
        return showDialog(help, help.querySelector('button'));
    }

    function field(body, id, label, values) {
        const wrapper = element('div', undefined, 'di-field');
        const caption = element('label', label);
        caption.htmlFor = id;
        const select = element('select');
        select.id = id;
        for (const [value, text] of values) {
            const option = element('option', text);
            option.value = value;
            select.appendChild(option);
        }
        wrapper.appendChild(caption);
        wrapper.appendChild(select);
        body.appendChild(wrapper);
        return select;
    }

    function mount() {
        if (mounted || !document.body) return;
        mounted = true;
        notifications = element('section', undefined, 'di-notifications');
        notifications.setAttribute('aria-label', 'Notifications');
        document.body.appendChild(notifications);

        palette = createDialog('di-command-dialog', 'Search commands');
        const paletteBody = element('div', undefined, 'di-dialog-body');
        const label = element('label', 'Find an available action');
        label.htmlFor = 'di-command-search';
        search = element('input');
        search.id = 'di-command-search';
        search.type = 'search';
        search.autocomplete = 'off';
        search.spellcheck = false;
        search.setAttribute('role', 'combobox');
        search.setAttribute('aria-autocomplete', 'list');
        search.setAttribute('aria-haspopup', 'listbox');
        search.setAttribute('aria-expanded', 'false');
        search.setAttribute('aria-controls', 'di-command-results');
        search.setAttribute('aria-describedby', 'di-command-hint di-command-status');
        const hint = element('p', 'Only actions supplied by this page are listed. Use ↑ / ↓ to choose, Enter to run, Escape to close.',
            'di-muted');
        hint.id = 'di-command-hint';
        results = element('div', undefined, 'di-command-results');
        results.id = 'di-command-results';
        results.setAttribute('role', 'listbox');
        results.setAttribute('aria-label', 'Available commands');
        resultStatus = element('p', '', 'di-muted');
        resultStatus.id = 'di-command-status';
        resultStatus.setAttribute('role', 'status');
        resultStatus.setAttribute('aria-live', 'polite');
        paletteBody.appendChild(label);
        paletteBody.appendChild(search);
        paletteBody.appendChild(results);
        paletteBody.appendChild(resultStatus);
        paletteBody.appendChild(hint);
        palette.appendChild(paletteBody);
        search.addEventListener('input', renderCommands);
        search.addEventListener('keydown', event => {
            if (event.isComposing || composing || event.keyCode === 229) return;
            if (['ArrowDown', 'ArrowUp'].includes(event.key)) {
                event.preventDefault();
                if (!visibleCommands.length) return;
                activeIndex = (activeIndex + (event.key === 'ArrowDown' ? 1 : -1) +
                    visibleCommands.length) % visibleCommands.length;
                updateActive();
            } else if (event.key === 'Enter' && visibleCommands[activeIndex]) {
                event.preventDefault();
                void execute(visibleCommands[activeIndex].id);
            }
        });

        appearance = createDialog('di-appearance-dialog', 'Appearance');
        const appearanceBody = element('div', undefined, 'di-dialog-body');
        const note = element('p', 'Only visual preferences are saved in this browser. Agent, tenant and document settings are unchanged.',
            'di-muted');
        note.id = 'di-appearance-note';
        appearance.setAttribute('aria-describedby', note.id);
        appearanceBody.appendChild(note);
        themeSelect = field(appearanceBody, 'di-theme', 'Theme', [
            ['system', 'System — follow device appearance'], ['light', 'Light'], ['dark', 'Dark']
        ]);
        densitySelect = field(appearanceBody, 'di-density', 'Density', [
            ['comfortable', 'Comfortable'], ['compact', 'Compact']
        ]);
        appearance.appendChild(appearanceBody);
        const footer = element('div', undefined, 'di-dialog-footer');
        footer.appendChild(button('Cancel', () => appearance.close()));
        footer.appendChild(button('Save appearance', () => {
            const patch = { theme: themeSelect.value, density: densitySelect.value };
            appearance.close();
            if (setPreferences(patch)) inform('Appearance saved in this browser.', { kind: 'success' });
        }, true));
        appearance.appendChild(footer);

        help = createDialog('di-shortcut-dialog', 'Keyboard shortcuts');
        const helpBody = element('div', undefined, 'di-dialog-body');
        const shortcuts = element('dl', undefined, 'di-shortcuts');
        for (const [keys, text] of [
            ['Ctrl+K / ⌘K', 'Search available commands'],
            ['?', 'Show keyboard help outside text fields'],
            ['↑ / ↓, Enter', 'Choose and run a palette command'],
            ['Escape', 'Close the current dialog'],
            ['Tab / Shift+Tab', 'Move between controls']
        ]) {
            const row = element('div');
            const term = element('dt');
            term.appendChild(element('kbd', keys));
            row.appendChild(term);
            row.appendChild(element('dd', text));
            shortcuts.appendChild(row);
        }
        helpBody.appendChild(shortcuts);
        helpBody.appendChild(element('p', 'Editor shortcuts and confirmation dialogs keep control of their own keyboard input.',
            'di-muted'));
        help.appendChild(helpBody);

        const tools = element('div', undefined, 'di-customer-tools');
        tools.setAttribute('role', 'group');
        tools.setAttribute('aria-label', 'Workspace tools');
        const commandButton = button('Commands', openCommands);
        commandButton.setAttribute('aria-haspopup', 'dialog');
        commandButton.setAttribute('aria-controls', palette.id);
        commandButton.setAttribute('aria-keyshortcuts', 'Control+k Meta+k');
        tools.appendChild(commandButton);
        const appearanceButton = button('Appearance', openPreferences);
        appearanceButton.setAttribute('aria-haspopup', 'dialog');
        appearanceButton.setAttribute('aria-controls', appearance.id);
        tools.appendChild(appearanceButton);
        const helpButton = button('Keyboard help', openShortcutHelp);
        helpButton.setAttribute('aria-haspopup', 'dialog');
        helpButton.setAttribute('aria-controls', help.id);
        tools.appendChild(helpButton);
        const host = document.querySelector('[data-docintel-ux-host]') ||
            document.querySelector('.di-page-actions, .meridian-actions, #harnessHeaderActions, .header-actions');
        if (host) host.appendChild(tools);
        else {
            tools.className += ' di-customer-tools--floating';
            document.body.appendChild(tools);
        }
        for (const notice of notices) renderNotice(notice);
        if (preferenceWarning) inform(preferenceWarning, { kind: 'warning' });
    }

    function keyboardTarget(event) {
        return event.composedPath?.()[0] || event.target;
    }

    document.addEventListener('compositionstart', () => { composing = true; });
    document.addEventListener('compositionend', () => { composing = false; });
    document.addEventListener('keydown', event => {
        if (!mounted || event.defaultPrevented || event.isComposing || composing || event.keyCode === 229 ||
            event.repeat || event.altKey) return;
        const target = keyboardTarget(event);
        const restricted = target?.isContentEditable || target?.closest?.(
            '[contenteditable]:not([contenteditable="false"]), [data-di-shortcuts="off"], [data-shortcuts="off"], ' +
            '.monaco-editor, .cm-editor, .CodeMirror, [role="textbox"], textarea, select, input[type="password"]');
        if (restricted || document.querySelector('dialog[open]')) return;
        if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k' && !event.shiftKey) {
            if (openCommands()) event.preventDefault();
        } else if (event.key === '?' && !event.ctrlKey && !event.metaKey &&
            !target?.closest?.('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) {
            if (openShortcutHelp()) event.preventDefault();
        }
    });

    globalThis.addEventListener('storage', event => {
        if (event.key !== preferenceKey && event.key !== null) return;
        try {
            const next = event.newValue === null
                ? { version: 1, theme: 'system', density: 'comfortable' } : JSON.parse(event.newValue);
            if (!validPreferences(next)) throw new TypeError('Invalid visual preferences.');
            preferences = { theme: next.theme, density: next.density };
            applyPreferences();
        } catch {
            inform('An appearance change from another tab was invalid and was not applied.', { kind: 'warning' });
        }
    });

    for (const [id, title, run] of [
        ['appearance', 'Change theme or density', openPreferences],
        ['shortcuts', 'Keyboard shortcuts', openShortcutHelp],
        ...themes.map(theme => [`theme.${theme}`, `Theme: ${theme}`, () => setPreferences({ theme })]),
        ...densities.map(density => [`density.${density}`, `Density: ${density}`, () => setPreferences({ density })])
    ]) commands.set(`docintel.${id}`, Object.freeze({ id: `docintel.${id}`, title, run }));

    globalThis.DocIntelUX = Object.freeze({
        registerCommands, inform, openCommands, openPreferences, openShortcutHelp, setPreferences,
        getPreferences: () => Object.freeze({ ...preferences })
    });
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount, { once: true });
    else mount();
})();
