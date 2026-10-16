import { OrderPage } from "@/components/order-page";
export const metadata = {
  title: "Your orders — CommercePilot",
  robots: { index: false, follow: false },
};
export default function Orders() {
  return <OrderPage />;
}
