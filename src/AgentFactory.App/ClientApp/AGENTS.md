# AGENTS.md — the Agent Factory app

## What this is

The React board for the Agent Factory, inside the .NET process that serves it.
It is a static export, so every page is a client component reading the factory's
loopback JSON API at `/api`.

## Rules that do not bend

- Import the one stylesheet once, in `app/layout.tsx`, before `app/globals.css`.
- Import Prism items from their own subpath, never the root barrel.
- Do not author Prism utility classes in this app. This app writes layout and
  spacing classes in `app/globals.css`; the design system supplies the rest.
- The app is one ground: `data-pack="lavender"` and `.dark` on the document.
  There is no pack switcher and no second pack region.
- The frame is Prism's console shell, `AppShell01`: a rail, a top bar with the
  trail and the fleet's pause control, and a sheet below `lg`. The two theme
  attributes and the shell's one provider are the whole theme runtime.
- The board's own surface is the working surface, not the marketing ground: the
  aurora wash arrives at Prism's `subtle` intensity inside the content column,
  and every panel above it is a glass pane from the pinned release.
- The only write path is the API's explicit commands. A page re-decides
  nothing: it renders what the API answers.

## Before changing a page

Read the corresponding API endpoint in
`../Api/AgentFactoryApi.cs`. The view records in `../Api/Views.cs` are the
shape; a field renamed there should fail `pnpm typecheck` here rather than read
`undefined` on screen.
