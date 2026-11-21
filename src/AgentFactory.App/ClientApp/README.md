# The Agent Factory app

The board for the Agent Factory: the fleet's runs, its pipelines, its targets
and its agent profiles, served as a static Next export and drawn with
`@nanisoft/prism-ui`.

## How it is built

- The app is a flat Next 16 static export (`output: 'export'`), served by the
  .NET process at the root beside the JSON API at `/api`.
- `@nanisoft/prism-ui/styles.css` is imported once, in `app/layout.tsx`, before
  this app's own sheet.
- The frame is Prism's `AppShell01`: the rail holds the four surfaces, the top
  bar holds the trail, the mode control and the fleet's pause command, and the
  same navigation arrives as a sheet below `lg`.
- The ground is one pack and one document: lavender, dark by default. There is
  no pack switcher and no scoped pack boundary. The rail's mark is the app's
  only pack boundary and it wears the ground.
- The working surface composes `AuroraWash` at `subtle` behind the content
  column only; the rail keeps a still ground. The panels above it are the
  pinned release's glass panes, translucent over that light.
- Items come from their own subpath: `@nanisoft/prism-ui/components/<item>` and
  `@nanisoft/prism-ui/blocks/<item>`, never the root barrel.
- This app's own classes are layout and spacing only. They live in
  `app/globals.css`; no Prism utility is authored here.

## Commands

```sh
pnpm install
pnpm dev       # Next dev server; proxies /api to http://127.0.0.1:5200
pnpm build     # static export to out/
pnpm lint
pnpm typecheck
pnpm test
pnpm check     # the Prism consumer gate kit; run after pnpm build
```
