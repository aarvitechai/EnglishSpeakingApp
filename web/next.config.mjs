/** @type {import('next').NextConfig} */
const isDev = process.env.NODE_ENV === "development";

const nextConfig = {
  // Served as static files by the API, so the whole product is one origin and one deployable.
  output: isDev ? undefined : "export",
  trailingSlash: true,
  images: { unoptimized: true },
  ...(isDev && {
    rewrites: async () => [{ source: "/api/:path*", destination: "http://localhost:5090/api/:path*" }],
  }),
};

export default nextConfig;
