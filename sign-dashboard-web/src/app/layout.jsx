import "./globals.css";

export const metadata = {
  title: "Sign Dashboard",
  description: "Sign shop CRM, tickets, inventory, quotes, and job orders"
};

export default function RootLayout({ children }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
