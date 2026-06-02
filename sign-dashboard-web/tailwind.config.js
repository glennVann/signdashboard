/** @type {import('tailwindcss').Config} */
const config = {
  content: ["./src/**/*.{js,jsx,mdx}"],
  theme: {
    extend: {
      colors: {
        ink: "#17212b",
        steel: "#425466",
        signal: "#f59e0b",
        mint: "#2f9e8f"
      }
    }
  },
  plugins: []
};

module.exports = config;
