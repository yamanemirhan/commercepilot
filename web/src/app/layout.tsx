import type { Metadata } from "next";
import "./globals.css";
import { ShoppingProvider } from "@/components/shopping";

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
      <body>
        <ShoppingProvider>{children}</ShoppingProvider>
      </body>
    </html>
  );
}
