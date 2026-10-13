import { Storefront } from "@/components/storefront";
import { ShoppingProvider } from "@/components/shopping";

export default function Home() {
  return (
    <ShoppingProvider>
      <Storefront />
    </ShoppingProvider>
  );
}
