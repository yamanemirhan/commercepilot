export type Product = {
  id: string;
  slug: string;
  name: string;
  description: string;
  category: string;
  price: number;
  currency: string;
  stock: number;
  artwork: string;
  color: string;
  badge: string | null;
};
export type CatalogPage = {
  items: Product[];
  total: number;
  page: number;
  pageSize: number;
};
export type Category = { name: string; count: number };
export const money = (amount: number, currency = "USD") =>
  new Intl.NumberFormat("en-US", {
    style: "currency",
    currency,
    maximumFractionDigits: 2,
    minimumFractionDigits: 0,
  }).format(amount);
