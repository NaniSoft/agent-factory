import type { NextConfig } from 'next';

// The Board is served by the factory's own .NET process at the root, so this app is
// a flat Next static export with no sub-path: `basePath` and `assetPrefix` are
// absent, every route is served from `/`, and the factory copies `out/` to
// `wwwroot` beside its entry assembly. `trailingSlash` makes the export emit a
// directory index per route (`projects/index.html`, `credentials/index.html`,
// `work-items/index.html`), which is what lets a direct load or refresh of
// `/projects` resolve through the .NET default-file serving rather than 404. The
// JSON it reads is the factory's own loopback API at `/api`.
const nextConfig: NextConfig = {
  output: 'export',
  trailingSlash: true,
  images: {
    unoptimized: true,
  },
};

export default nextConfig;
