import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "PokeCreator",
  description: "Create legal Pokemon using PKHeX data",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
