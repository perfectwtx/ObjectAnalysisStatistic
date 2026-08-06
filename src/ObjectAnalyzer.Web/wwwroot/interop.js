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
    }
};
