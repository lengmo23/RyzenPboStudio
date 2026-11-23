const picker = document.querySelector(".language-picker");
const summary = picker.querySelector("summary");
const languages = picker.querySelectorAll("a[hreflang]");

function updateLanguageLinks() {
  for (const link of languages) {
    const url = new URL(link.href);
    url.hash = location.hash;
    link.href = url.href;
  }
}

updateLanguageLinks();
window.addEventListener("hashchange", updateLanguageLinks);

document.addEventListener("click", event => {
  if (picker.open && !picker.contains(event.target)) picker.open = false;
});

picker.addEventListener("keydown", event => {
  if (event.key === "Escape" && picker.open) {
    picker.open = false;
    summary.focus();
  }
});
