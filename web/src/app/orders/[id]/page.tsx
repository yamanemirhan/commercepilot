import { notFound } from "next/navigation";
import { OrderPage } from "@/components/order-page";
export const metadata = {
  title: "Order details — CommercePilot",
  robots: { index: false, follow: false },
};
export default async function Order({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)
  )
    notFound();
  return <OrderPage orderId={id} />;
}
