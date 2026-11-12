// Small host helpers. Kept intentionally minimal: all logic lives in .NET services.
window.u7d2d = {
    focusSearch: function () {
        const input = document.querySelector('.search-box input');
        if (input) input.focus();
    },
    scrollTop: function () {
        const content = document.querySelector('.content');
        if (content) content.scrollTo({ top: 0, behavior: 'smooth' });
    }
};

document.addEventListener('keydown', function (e) {
    if (e.key === '/' && document.activeElement.tagName !== 'INPUT') {
        e.preventDefault();
        window.u7d2d.focusSearch();
    }
});
