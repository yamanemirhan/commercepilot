import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "CommercePilot — Good things for your everyday",
  description:
    "A considered collection of audio, workspace, and everyday essentials.",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
