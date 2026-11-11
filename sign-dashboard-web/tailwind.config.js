/** @type {import('tailwindcss').Config} */
const config = {
  content: ["./src/**/*.{js,jsx,mdx}"],
  theme: {
    extend: {
      colors: {
        ink: "#0F172A",
        steel: "#334155",
        signal: "#F97316",
        mint: "#2563EB"
      }
    }
  },
  plugins: []
};

module.exports = config;
