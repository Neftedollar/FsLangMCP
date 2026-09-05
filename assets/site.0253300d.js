(() => {
    const mainItems = document.querySelector(
        ".nacara-navbar__items:not(.nacara-navbar__items--end)",
    );

    // Nacara 3 beta renders role=navigation directly on a ul, which suppresses
    // the list semantics of its li children. Preserve both landmarks.
    if (mainItems?.getAttribute("role") === "navigation") {
        const navigation = document.createElement("nav");
        navigation.setAttribute(
            "aria-label",
            mainItems.getAttribute("aria-label") || "Main",
        );
        mainItems.removeAttribute("role");
        mainItems.removeAttribute("aria-label");
        mainItems.before(navigation);
        navigation.appendChild(mainItems);
    }

    const search = document.querySelector(".nacara-search__trigger");
    const searchLabel = search?.textContent?.replace(/\s+/g, " ").trim();

    if (search && searchLabel) {
        search.setAttribute("aria-label", searchLabel);
    }

    const colourScheme = document.querySelector("[data-nacara-theme]");

    if (colourScheme) {
        colourScheme.id = "nacara-colour-scheme";
        colourScheme.setAttribute("name", "colour-scheme");
    }
})();
