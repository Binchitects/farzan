// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
export type Theme = "auto" | "light" | "dark";

export function currentTheme(): Theme {
  try {
    return (localStorage.getItem("code-intel.theme") as Theme) ?? "auto";
  } catch {
    return "auto";
  }
}

export function applyTheme(theme: Theme) {
  if (theme === "auto") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);
  try {
    localStorage.setItem("code-intel.theme", theme);
  } catch {
    /* private window */
  }
}
