// Words the scripts show, in the dashboard's language: Components/Layout/MainLayout.razor renders them as data
// attributes of #ui-texts (data-view-unavailable → uiText('viewUnavailable', …)). {0}, {1}… take the arguments.
export function uiText(name, fallback, ...args) {
    const template = document.getElementById('ui-texts')?.dataset[name] ?? fallback;
    return template.replace(/\{(\d+)\}/g, (_, index) => String(args[Number(index)] ?? ''));
}
