/* ==========================================================================
   Dashboard Page Logic
   ========================================================================== */

let lastActivityCount = 0;

async function refreshData() {
    await Promise.all([loadStats(), loadTrends(), loadActivity(), checkHealth(), loadInsights(), loadAdaptiveStats(), loadHooksStatus()]);
}

// ---- Health Check (from completeness) ----
async function checkHealth() {
    try {
        const response = await fetch('/health');
        const data = await response.json();
        const indicator = document.querySelector('.status-indicator');
        const statusLabel = document.querySelector('.status-label');
        if (data.status === 'healthy') {
            if (indicator) indicator.classList.add('active');
            if (statusLabel) statusLabel.textContent = 'Connected';
        } else {
            if (indicator) indicator.classList.remove('active');
            if (statusLabel) statusLabel.textContent = 'Service Degraded';
        }
    } catch {
        const indicator = document.querySelector('.status-indicator');
        if (indicator) indicator.classList.remove('active');
        const statusLabel = document.querySelector('.status-label');
        if (statusLabel) statusLabel.textContent = 'Disconnected';
    }
}

// ---- Stats ----
async function loadStats() {
    const cards = document.querySelectorAll('.stat-card');
    cards.forEach(c => c.classList.add('loading'));

    try {
        const data = await fetchApi('/api/dashboard/stats');

        document.getElementById('autoApprovedCount').textContent = data.autoApprovedToday;
        document.getElementById('deniedCount').textContent = data.deniedToday;
        document.getElementById('activeSessionCount').textContent = data.activeSessions;
        document.getElementById('avgSafetyScore').textContent = data.avgSafetyScore;

        cards.forEach(c => c.classList.remove('loading'));
    } catch (error) {
        cards.forEach(c => c.classList.remove('loading'));
        showErrorState('statsError', 'Failed to load statistics', error.message);
    }
}

// ---- Trend Chart ----
async function loadTrends() {
    try {
        const trends = await fetchApi('/api/dashboard/trends?days=7');
        renderBarChart(trends);
    } catch (error) {
        const chart = document.getElementById('trendChart');
        if (chart) {
            chart.innerHTML = '<div class="empty-state"><p>Could not load trend data</p></div>';
        }
    }
}

function renderBarChart(trends) {
    const chart = document.getElementById('trendChart');
    if (!chart || !trends.length) return;

    const maxTotal = Math.max(...trends.map(t => t.approved + t.denied), 1);

    chart.innerHTML = trends.map(t => {
        const approvedH = ((t.approved / maxTotal) * 100);
        const deniedH = ((t.denied / maxTotal) * 100);
        const dayLabel = new Date(t.date).toLocaleDateString(undefined, { weekday: 'short' });

        return `
            <div class="bar-group" title="${t.date}: ${t.approved} approved, ${t.denied} denied">
                <div class="bar-stack">
                    <div class="bar denied" style="height: ${deniedH}%"></div>
                    <div class="bar approved" style="height: ${approvedH}%"></div>
                </div>
                <span class="bar-label">${dayLabel}</span>
            </div>
        `;
    }).join('');
}

// ---- Score Distribution ----
async function loadScoreDistribution(events) {
    const donut = document.getElementById('scoreDonut');
    if (!donut) return;

    const scored = events.filter(e => e.safetyScore != null);
    if (scored.length === 0) {
        donut.style.background = 'var(--border-color)';
        const center = donut.querySelector('.donut-center');
        if (center) {
            center.innerHTML = '0<small>events</small>';
        }
        return;
    }

    const safe = scored.filter(e => e.safetyScore >= 70).length;
    const cautious = scored.filter(e => e.safetyScore >= 40 && e.safetyScore < 70).length;
    const risky = scored.filter(e => e.safetyScore < 40).length;
    const total = scored.length;

    const safeDeg = (safe / total) * 360;
    const cautiousDeg = safeDeg + (cautious / total) * 360;

    donut.style.setProperty('--safe-deg', `${safeDeg}deg`);
    donut.style.setProperty('--cautious-deg', `${cautiousDeg}deg`);

    const center = donut.querySelector('.donut-center');
    if (center) {
        center.innerHTML = `${total}<small>events</small>`;
    }

    document.getElementById('safeCount').textContent = safe;
    document.getElementById('cautiousCount').textContent = cautious;
    document.getElementById('riskyCount').textContent = risky;
}

// ---- Activity Feed ----
async function loadActivity() {
    const list = document.getElementById('activityList');
    if (!list) return;

    try {
        const events = await fetchApi('/api/dashboard/activity?limit=20');

        if (events.length === 0) {
            list.innerHTML = `
                <div class="empty-state">
                    <div class="empty-state-icon">\u{1F50D}</div>
                    <h3>No activity yet</h3>
                    <p>Permission analysis events will appear here as Claude Code or Copilot CLI makes tool calls through the hook system.</p>
                </div>
            `;
            return;
        }

        // Check for new denied events (toast notification)
        if (lastActivityCount > 0) {
            const newDenied = events.filter((e, i) => i < events.length - lastActivityCount && e.decision === 'denied');
            newDenied.forEach(e => {
                Toast.show(
                    'Permission Denied',
                    `${e.toolName || 'Unknown tool'} was denied (score: ${e.safetyScore || 'N/A'})`,
                    'danger'
                );
            });
        }
        lastActivityCount = events.length;

        list.innerHTML = events.map(e => {
            const decisionClass = getDecisionClass(e.decision);
            const categoryClass = e.category === 'dangerous' ? 'denied' :
                                  e.category === 'risky' ? 'cautious' : '';

            return `
                <div class="activity-item ${decisionClass} ${categoryClass}" role="article">
                    <div class="activity-header">
                        <span class="activity-tool">${escapeHtml(e.toolName || 'Unknown')}</span>
                        <span class="activity-decision ${decisionClass}">${getDecisionLabel(e.decision)}</span>
                    </div>
                    ${e.reasoning ? `<div class="activity-reasoning">${escapeHtml(e.reasoning)}</div>` : ''}
                    <div class="activity-meta">
                        <span class="activity-time">${formatTime(e.timestamp)}</span>
                        ${e.safetyScore != null ? `<span class="activity-score ${getScoreClass(e.safetyScore)}">Score: ${e.safetyScore}</span>` : ''}
                        ${e.category ? `<span class="activity-category">${escapeHtml(e.category)}</span>` : ''}
                    </div>
                </div>
            `;
        }).join('');

        loadScoreDistribution(events);
    } catch (error) {
        list.innerHTML = `
            <div class="error-state">
                <h3>Failed to load activity</h3>
                <p>${escapeHtml(error.message)}</p>
                <button class="btn" onclick="loadActivity()">Retry</button>
            </div>
        `;
    }
}

function showErrorState(id, title, message) {
    const el = document.getElementById(id);
    if (el) {
        el.innerHTML = `
            <div class="error-state">
                <h3>${escapeHtml(title)}</h3>
                <p>${escapeHtml(message)}</p>
                <button class="btn" onclick="refreshData()">Retry</button>
            </div>
        `;
    }
}

function escapeHtml(str) {
    if (!str) return '';
    const div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
}

// ===========================================================================
// Creative Features: Profile Switcher, Insights, Adaptive Thresholds, Quick Actions
// ===========================================================================

// ---- Profile Switcher ----
function initProfileSwitcher() {
    const cards = document.querySelectorAll('.profile-card');
    cards.forEach(function (card) {
        card.addEventListener('click', function () {
            const profileKey = this.getAttribute('data-profile');
            fetchApi('/api/profile/switch', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ profileKey: profileKey })
            })
                .then(function (data) {
                    cards.forEach(function (c) { c.classList.remove('active'); });
                    card.classList.add('active');
                    Toast.show('Profile Switched', 'Switched to ' + data.profile.name + ' profile', 'success');
                })
                .catch(function (err) {
                    Toast.show('Profile Error', 'Failed to switch profile: ' + err.message, 'danger');
                });
        });
    });

    // Load active profile
    fetchApi('/api/profile')
        .then(function (data) {
            cards.forEach(function (c) { c.classList.remove('active'); });
            const activeCard = document.querySelector('[data-profile="' + data.activeProfile + '"]');
            if (activeCard) activeCard.classList.add('active');
        })
        .catch(function () { /* silently fail on initial load */ });
}

// ---- Insights Panel ----
function loadInsights() {
    fetchApi('/api/insights')
        .then(function (data) {
            const list = document.getElementById('insightsList');
            const countEl = document.getElementById('insightCount');
            if (!list || !countEl) return;

            countEl.textContent = data.count;

            if (data.insights.length === 0) {
                list.innerHTML = '<p class="empty-state">No insights yet. Keep using the analyzer to generate recommendations.</p>';
                return;
            }

            list.innerHTML = '';
            data.insights.forEach(function (insight) {
                const item = document.createElement('div');
                item.className = 'insight-item severity-' + insight.severity;
                item.innerHTML =
                    '<div class="insight-content">' +
                    '<div class="insight-title">' + escapeHtml(insight.title) + '</div>' +
                    '<div class="insight-desc">' + escapeHtml(insight.description) + '</div>' +
                    '<div class="insight-recommendation">' + escapeHtml(insight.recommendation) + '</div>' +
                    '</div>' +
                    '<button class="insight-dismiss" data-id="' + escapeHtml(insight.id) + '" title="Dismiss">&times;</button>';
                list.appendChild(item);
            });

            // Bind dismiss buttons
            list.querySelectorAll('.insight-dismiss').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    const id = this.getAttribute('data-id');
                    fetchApi('/api/insights/dismiss/' + id, { method: 'POST' })
                        .then(function () {
                            btn.closest('.insight-item').remove();
                            const current = parseInt(countEl.textContent) || 0;
                            countEl.textContent = Math.max(0, current - 1);
                        });
                });
            });
        })
        .catch(function () { /* silently fail */ });
}

// ---- Adaptive Thresholds Display ----
function loadAdaptiveStats() {
    fetchApi('/api/adaptivethreshold/stats')
        .then(function (data) {
            const grid = document.getElementById('adaptiveStats');
            if (!grid) return;

            if (!data.toolStats || data.toolStats.length === 0) {
                grid.innerHTML = '<p class="empty-state">No tool data yet. Decisions will appear here as the analyzer processes requests.</p>';
                return;
            }

            grid.innerHTML = '';
            data.toolStats.forEach(function (stat) {
                const confidencePct = Math.round(stat.confidenceLevel * 100);
                const scoreColor = stat.averageSafetyScore >= 90 ? 'var(--color-success)' :
                    stat.averageSafetyScore >= 70 ? 'var(--color-warning)' : 'var(--color-danger)';

                const card = document.createElement('div');
                card.className = 'adaptive-card';
                card.innerHTML =
                    '<div class="adaptive-tool-name">' + escapeHtml(stat.toolName) + '</div>' +
                    '<div class="adaptive-stats-row"><span>Decisions</span><span>' + stat.totalDecisions + '</span></div>' +
                    '<div class="adaptive-stats-row"><span>Overrides</span><span>' + stat.overrideCount + '</span></div>' +
                    '<div class="adaptive-stats-row"><span>Avg Score</span><span style="color:' + scoreColor + '">' + stat.averageSafetyScore + '</span></div>' +
                    '<div class="adaptive-bar"><div class="adaptive-bar-fill" style="width:' + confidencePct + '%;background:' + scoreColor + '"></div></div>' +
                    '<div class="adaptive-stats-row"><span>Confidence</span><span>' + confidencePct + '%</span></div>' +
                    (stat.suggestedThreshold != null ?
                        '<div class="adaptive-suggestion">Suggested threshold: ' + stat.suggestedThreshold + '</div>' : '');
                grid.appendChild(card);
            });
        })
        .catch(function () { /* silently fail */ });
}

// ---- Quick Actions ----
function initQuickActions() {
    const btnTrust = document.getElementById('btnTrustSession');
    const btnReset = document.getElementById('btnReset');
    const btnLockdown = document.getElementById('btnLockdown');
    const btnReport = document.getElementById('btnReport');

    if (btnTrust) {
        btnTrust.addEventListener('click', function () {
            fetchApi('/api/quickactions/trust-session', { method: 'POST' })
                .then(function (data) {
                    Toast.show('Session Trusted', data.message, 'success');
                    highlightProfile('permissive');
                })
                .catch(function (err) { Toast.show('Action Failed', 'Failed: ' + err.message, 'danger'); });
        });
    }

    if (btnReset) {
        btnReset.addEventListener('click', function () {
            fetchApi('/api/quickactions/reset', { method: 'POST' })
                .then(function (data) {
                    Toast.show('Reset Complete', data.message, 'info');
                    highlightProfile('moderate');
                })
                .catch(function (err) { Toast.show('Action Failed', 'Failed: ' + err.message, 'danger'); });
        });
    }

    if (btnLockdown) {
        btnLockdown.addEventListener('click', function () {
            fetchApi('/api/quickactions/lockdown', { method: 'POST' })
                .then(function (data) {
                    Toast.show('Lockdown Active', data.message, 'warning');
                    highlightProfile('lockdown');
                })
                .catch(function (err) { Toast.show('Action Failed', 'Failed: ' + err.message, 'danger'); });
        });
    }

    if (btnReport) {
        btnReport.addEventListener('click', function () {
            const sessionId = prompt('Enter session ID for audit report:');
            if (sessionId) {
                window.open('/api/auditreport/' + encodeURIComponent(sessionId) + '/html', '_blank');
            }
        });
    }
}

function highlightProfile(key) {
    const cards = document.querySelectorAll('.profile-card');
    cards.forEach(function (c) { c.classList.remove('active'); });
    const card = document.querySelector('[data-profile="' + key + '"]');
    if (card) card.classList.add('active');
}

// ---- Hooks Management ----
var currentEnforcementMode = 'observe';

async function loadHooksStatus() {
    try {
        var data = await fetchApi('/api/hooks/status');
        var mode = data.enforcementMode || (data.enforced ? 'enforce' : 'observe');
        currentEnforcementMode = mode;
        updateHooksUI(data.installed);
        updateEnforcementModeUI(mode);
        updateCopilotHooksUI(data.copilot);
    } catch {
        // Service may not support hooks endpoint yet
    }
}

function updateHooksUI(installed) {
    var installedBadge = document.getElementById('hookInstalledBadge');
    var btnToggleHooks = document.getElementById('btnToggleHooks');

    if (installedBadge) {
        installedBadge.textContent = installed ? 'Installed' : 'Not Installed';
        installedBadge.className = 'hook-status-badge ' + (installed ? 'badge-success' : 'badge-neutral');
    }
    if (btnToggleHooks) {
        btnToggleHooks.textContent = installed ? 'Uninstall Hooks' : 'Install Hooks';
        btnToggleHooks.disabled = false;
    }
}

function updateEnforcementModeUI(mode) {
    var cards = document.querySelectorAll('.enforcement-mode-card');
    cards.forEach(function (card) {
        card.classList.remove('active', 'switching');
    });
    var activeCard = document.querySelector('.enforcement-mode-card[data-mode="' + mode + '"]');
    if (activeCard) activeCard.classList.add('active');

    // Update connector fill to show progress
    var fill = document.getElementById('enforcementConnectorFill');
    if (fill) {
        var fillPct = { 'observe': '0', 'approve-only': '50', 'enforce': '100' };
        fill.style.width = (fillPct[mode] || '0') + '%';
    }
}

function initEnforcementModeCards() {
    var cards = document.querySelectorAll('.enforcement-mode-card');
    cards.forEach(function (card) {
        card.addEventListener('click', function () {
            var targetMode = this.getAttribute('data-mode');
            if (targetMode === currentEnforcementMode) return;

            // Visual feedback: mark all cards as switching
            cards.forEach(function (c) { c.classList.add('switching'); });

            fetchApi('/api/hooks/enforce?mode=' + encodeURIComponent(targetMode), { method: 'POST' })
                .then(function (data) {
                    currentEnforcementMode = data.enforcementMode;
                    updateEnforcementModeUI(data.enforcementMode);
                    Toast.show('Enforcement Mode', data.message, 'success');
                })
                .catch(function (err) {
                    Toast.show('Mode Error', 'Failed: ' + err.message, 'danger');
                    cards.forEach(function (c) { c.classList.remove('switching'); });
                });
        });
    });
}

function updateCopilotHooksUI(copilotStatus) {
    var badge = document.getElementById('copilotInstalledBadge');
    var btn = document.getElementById('btnToggleCopilotHooks');

    if (!copilotStatus) {
        if (badge) { badge.textContent = 'Unknown'; badge.className = 'hook-status-badge badge-neutral'; }
        if (btn) { btn.textContent = 'Install Hooks'; btn.disabled = false; }
        return;
    }

    var installed = copilotStatus.userInstalled;
    if (badge) {
        badge.textContent = installed ? 'Installed' : 'Not Installed';
        badge.className = 'hook-status-badge ' + (installed ? 'badge-copilot' : 'badge-neutral');
    }
    if (btn) {
        btn.textContent = installed ? 'Uninstall Hooks' : 'Install Hooks';
        btn.disabled = false;
    }
}

function initHooksControls() {
    var btnToggleHooks = document.getElementById('btnToggleHooks');

    if (btnToggleHooks) {
        btnToggleHooks.addEventListener('click', function () {
            var isInstalled = document.getElementById('hookInstalledBadge').textContent === 'Installed';
            var endpoint = isInstalled ? '/api/hooks/uninstall' : '/api/hooks/install';
            btnToggleHooks.disabled = true;
            fetchApi(endpoint, { method: 'POST' })
                .then(function (data) {
                    Toast.show('Hooks', data.message, 'success');
                    loadHooksStatus();
                })
                .catch(function (err) {
                    Toast.show('Hooks Error', 'Failed: ' + err.message, 'danger');
                    btnToggleHooks.disabled = false;
                });
        });
    }

    // Copilot hooks controls
    var copilotLevelRadios = document.querySelectorAll('input[name="copilotLevel"]');
    var repoPathGroup = document.getElementById('copilotRepoPathGroup');
    copilotLevelRadios.forEach(function (radio) {
        radio.addEventListener('change', function () {
            if (repoPathGroup) {
                repoPathGroup.style.display = this.value === 'repo' ? 'block' : 'none';
            }
        });
    });

    var btnToggleCopilot = document.getElementById('btnToggleCopilotHooks');
    if (btnToggleCopilot) {
        btnToggleCopilot.addEventListener('click', function () {
            var badge = document.getElementById('copilotInstalledBadge');
            var isInstalled = badge && badge.textContent === 'Installed';
            var level = document.querySelector('input[name="copilotLevel"]:checked');
            var levelValue = level ? level.value : 'user';
            var repoPath = document.getElementById('copilotRepoPath');
            var repoPathValue = repoPath ? repoPath.value.trim() : '';

            var endpoint = isInstalled ? '/api/hooks/copilot/uninstall' : '/api/hooks/copilot/install';
            var params = '?level=' + encodeURIComponent(levelValue);
            if (levelValue === 'repo' && repoPathValue) {
                params += '&repoPath=' + encodeURIComponent(repoPathValue);
            }

            btnToggleCopilot.disabled = true;
            fetchApi(endpoint + params, { method: 'POST' })
                .then(function (data) {
                    Toast.show('Copilot Hooks', data.message, 'success');
                    loadHooksStatus();
                })
                .catch(function (err) {
                    Toast.show('Copilot Hooks Error', 'Failed: ' + err.message, 'danger');
                    btnToggleCopilot.disabled = false;
                });
        });
    }
}

// ---- Auto-refresh ----
let refreshInterval;

document.addEventListener('DOMContentLoaded', () => {
    // Initialize creative features
    initProfileSwitcher();
    initQuickActions();
    initHooksControls();
    initEnforcementModeCards();

    // Load all data
    refreshData();
    refreshInterval = setInterval(refreshData, 30000);
});

document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        clearInterval(refreshInterval);
    } else {
        refreshData();
        refreshInterval = setInterval(refreshData, 30000);
    }
});
