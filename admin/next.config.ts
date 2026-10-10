import type { NextConfig } from 'next';

// The Board is served by the factory's own .NET process at `/admin`, so this app
// is a flat Next static export under a sub-path: `basePath` moves every route and
// `assetPrefix` moves every emitted asset beneath `/admin`, and the factory
// copies `out/` to `wwwroot/admin` beside its entry assembly. `out/` itself stays
// flat — a base path changes the URLs Next writes, not the layout it exports — and
// the JSON it reads is the factory's own loopback API at `/api`, off the base path.
const basePath = '/admin';

const nextConfig: NextConfig = {
  output: 'export',
  basePath,
  assetPrefix: basePath,
  images: {
    unoptimized: true,
  },
};

export default nextConfig;
