import type { NextConfig } from 'next';

// The Agent Factory serves this app from its own .NET process at the root, so
// the app is a flat static export with no sub-path. `trailingSlash` emits a
// directory index per route, and `scripts/emit-route-aliases.mjs` writes the
// `.html` spelling beside each one for a cold load without the slash.
const nextConfig: NextConfig = {
  output: 'export',
  trailingSlash: true,
  images: {
    unoptimized: true,
  },
};

// `next dev` runs on its own port; the factory's JSON surface is the .NET
// process. The rewrite keeps the browser on one origin in development and is
// absent from the static export, which is served by the .NET process itself.
if (process.env.NODE_ENV === 'development') {
  nextConfig.rewrites = async () => [
    {
      source: '/api/:path*',
      destination: `${process.env.AGENT_FACTORY_API_ORIGIN ?? 'http://127.0.0.1:5200'}/api/:path*`,
    },
  ];
}

export default nextConfig;
