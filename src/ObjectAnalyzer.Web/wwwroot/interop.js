window.objectAnalyzer = {
    downloadTextFile: function (fileName, content, mimeType) {
        const type = mimeType || 'application/json;charset=utf-8';
        const blob = new Blob([content], { type: type });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    },

    // 渲染字段覆盖率柱状图（Chart.js），重复调用时销毁旧实例避免叠加
    renderCoverageChart: function (fieldNames, coveragePercent) {
        const ctx = document.getElementById('coverageChart');
        if (!ctx || typeof Chart === 'undefined') return;
        if (window.__coverageChart) window.__coverageChart.destroy();
        window.__coverageChart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: fieldNames,
                datasets: [{
                    label: '覆盖率 (%)',
                    data: coveragePercent,
                    backgroundColor: 'rgba(54,162,235,0.6)',
                    borderColor: 'rgba(54,162,235,1)',
                    borderWidth: 1
                }]
            },
            options: {
                responsive: true,
                plugins: { legend: { display: false } },
                scales: { y: { beginAtZero: true, max: 100 } }
            }
        });
    },

    // 渲染字段类型分布堆叠柱状图（Top N 字段）
    renderTypeChart: function (labels, typeDatasets) {
        const ctx = document.getElementById('typeChart');
        if (!ctx || typeof Chart === 'undefined') return;
        if (window.__typeChart) window.__typeChart.destroy();
        window.__typeChart = new Chart(ctx, {
            type: 'bar',
            data: { labels: labels, datasets: typeDatasets },
            options: {
                responsive: true,
                scales: { x: { stacked: true }, y: { stacked: true } }
            }
        });
    },

    // 主题切换（浅色/深色），依赖 Bootstrap 5.3+ 的 data-bs-theme 属性
    isDarkTheme: function () {
        return document.documentElement.getAttribute('data-bs-theme') === 'dark';
    },

    setTheme: function (theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        try { localStorage.setItem('oa-theme', theme); } catch (e) { }
    }
};
