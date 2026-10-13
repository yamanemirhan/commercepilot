"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import {
  ArrowDown,
  ArrowLeft,
  ArrowRight,
  ArrowUpRight,
  Check,
  ChevronDown,
  Headphones,
  Laptop,
  Layers3,
  LoaderCircle,
  Search,
  SlidersHorizontal,
  Sparkles,
  X,
} from "lucide-react";
import { ProductArt } from "./product-art";
import { AddToBag, ShoppingControls } from "./shopping";
import { CatalogPage, Category, money, Product } from "@/lib/catalog";

const categoryNames = [
  "All essentials",
  "Audio",
  "Workspace",
  "Everyday",
  "Gaming",
];

export function Storefront() {
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  const [category, setCategory] = useState("All essentials");
  const [sort, setSort] = useState("featured");
  const [maxPrice, setMaxPrice] = useState("all");
  const [inStock, setInStock] = useState(false);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<CatalogPage | null>(null);
  const [categories, setCategories] = useState<Category[]>([]);
  const [featured, setFeatured] = useState<Product | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [retry, setRetry] = useState(0);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [detail, setDetail] = useState<Product | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState(false);
  const productDialog = useRef<HTMLDialogElement>(null);
  const aboutDialog = useRef<HTMLDialogElement>(null);
  const detailRequest = useRef<AbortController | null>(null);

  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearch(search);
      setPage(1);
    }, 300);
    return () => clearTimeout(timer);
  }, [search]);

  useEffect(() => {
    const controller = new AbortController();
    fetch("/api/catalog/categories", { signal: controller.signal })
      .then((r) => {
        if (!r.ok) throw new Error("Categories unavailable");
        return r.json();
      })
      .then(setCategories)
      .catch(() => {});
    fetch("/api/catalog/products/10000000-0000-0000-0000-000000000001", {
      signal: controller.signal,
    })
      .then((response) => {
        if (!response.ok) throw new Error("Featured product unavailable");
        return response.json();
      })
      .then(setFeatured)
      .catch(() => {
        if (!controller.signal.aborted) setFeatured(null);
      });
    return () => controller.abort();
  }, [retry]);

  useEffect(() => {
    const controller = new AbortController();
    const query = new URLSearchParams({
      sort,
      page: String(page),
      pageSize: "12",
    });
    if (debouncedSearch.trim()) query.set("search", debouncedSearch.trim());
    if (category !== "All essentials") query.set("category", category);
    if (maxPrice !== "all") query.set("maxPrice", maxPrice);
    if (inStock) query.set("inStock", "true");
    setLoading(true);
    setError(null);
    fetch(`/api/catalog/products?${query}`, { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok)
          throw new Error(
            "We couldn’t load the collection. Please try again in a moment.",
          );
        return response.json() as Promise<CatalogPage>;
      })
      .then(setData)
      .catch((e) => {
        if (!controller.signal.aborted) {
          setError(e.message);
          setData(null);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [debouncedSearch, category, sort, page, maxPrice, inStock, retry]);

  useEffect(() => () => detailRequest.current?.abort(), []);

  const selectCategory = useCallback((name: string) => {
    setCategory(name);
    setPage(1);
  }, []);
  function explore(name = "All essentials") {
    selectCategory(name);
    document
      .getElementById("collection")
      ?.scrollIntoView({ behavior: "smooth" });
  }
  function clearFilters() {
    setSearch("");
    setDebouncedSearch("");
    setCategory("All essentials");
    setMaxPrice("all");
    setInStock(false);
    setPage(1);
  }
  async function openProduct(id: string) {
    detailRequest.current?.abort();
    const controller = new AbortController();
    detailRequest.current = controller;
    setDetail(null);
    setDetailError(false);
    setDetailLoading(true);
    productDialog.current?.showModal();
    try {
      const response = await fetch(`/api/catalog/products/${id}`, {
        signal: controller.signal,
      });
      if (!response.ok) throw new Error("Product unavailable");
      setDetail(await response.json());
    } catch {
      if (!controller.signal.aborted) setDetailError(true);
    } finally {
      if (!controller.signal.aborted) setDetailLoading(false);
    }
  }
  const activeFilters = (maxPrice !== "all" ? 1 : 0) + (inStock ? 1 : 0);
  const totalPages = data ? Math.ceil(data.total / data.pageSize) : 0;

  return (
    <>
      <a className="skip-link" href="#collection">
        Skip to collection
      </a>
      <div className="announcement">
        <span>A little more considered. A little more you.</span>
        <span className="announcement-label">
          THE EVERYDAY COLLECTION <ArrowUpRight size={12} />
        </span>
      </div>
      <header className="site-header shell">
        <a href="/" className="brand" aria-label="CommercePilot home">
          <span className="brand-mark">
            <Layers3 size={23} strokeWidth={1.8} />
          </span>
          commerce<span className="brand-light">pilot</span>
          <span className="brand-dot">®</span>
        </a>
        <nav aria-label="Main navigation">
          <button onClick={() => explore()}>Discover</button>
          <button onClick={() => explore("Audio")}>Audio</button>
          <button onClick={() => explore("Workspace")}>Workspace</button>
          <button onClick={() => aboutDialog.current?.showModal()}>
            Our approach <ArrowUpRight size={12} />
          </button>
        </nav>
        <ShoppingControls />
      </header>

      <main>
        <section className="hero shell" aria-labelledby="hero-title">
          <div className="hero-copy">
            <div className="eyebrow">
              <span className="little-line" /> LESS NOISE. MORE GOOD STUFF.
            </div>
            <h1 id="hero-title">
              Good things.
              <br />
              For your <span>everyday.</span>
            </h1>
            <p>
              Thoughtful tech. Considered design. Discover the
              <br className="desktop-break" /> essentials that make your day
              feel a little better.
            </p>
            <div className="hero-actions">
              <button className="button dark" onClick={() => explore()}>
                Explore the collection <ArrowUpRight size={18} />
              </button>
              <span className="small-note">Find your next favorite.</span>
            </div>
            <div className="hero-footnote">
              <span className="tiny-stars">✳</span> Designed around life. Chosen
              with care.
            </div>
          </div>
          <div className="hero-visual">
            <div className="visual-top">
              <span>THE LISTENING EDIT</span>
              <span>01 — 04</span>
            </div>
            <div className="hero-circle" />
            <span className="hero-background-word">unwind.</span>
            <ProductArt kind="headphones" className="hero-headphones" />
            <span className="floating-note">
              <span className="note-dot" /> A little peace.
              <br />
              <span className="note-indent">Wherever you go.</span>
            </span>
            <button
              className="hero-product"
              onClick={() =>
                featured ? openProduct(featured.id) : explore("Audio")
              }
            >
              <span>
                <small>MEET YOUR EVERYDAY ESCAPE</small>
                <strong>
                  {featured ? featured.name : "Explore audio essentials"}
                  {featured && (
                    <span>{money(featured.price, featured.currency)}</span>
                  )}
                </strong>
              </span>
              <span className="circle-arrow">
                <ArrowUpRight size={21} />
              </span>
            </button>
          </div>
        </section>

        <div className="values-strip shell">
          <span>
            <Layers3 size={16} /> Everyday, elevated
          </span>
          <i />
          <span>
            <Headphones size={17} /> Made for your rhythm
          </span>
          <i />
          <span>
            <Laptop size={17} /> A space to do your best
          </span>
          <i />
          <span>
            <Sparkles size={16} /> Less, but better
          </span>
        </div>

        <section
          id="collection"
          className="collection shell"
          aria-labelledby="collection-title"
        >
          <div className="collection-heading">
            <div>
              <div className="eyebrow">THE CONSIDERED COLLECTION</div>
              <h2 id="collection-title">
                Your everyday, upgraded<span>.</span>
              </h2>
            </div>
            <span className="collection-note">
              Little things. Big difference. <ArrowDown size={16} />
            </span>
          </div>
          <div className="catalog-toolbar">
            <div className="category-tabs" aria-label="Product categories">
              {categoryNames.map((name) => (
                <button
                  key={name}
                  className={category === name ? "active" : ""}
                  aria-pressed={category === name}
                  onClick={() => selectCategory(name)}
                >
                  {name}
                  {name === "All essentials" && categories.length > 0 && (
                    <span>
                      {categories.reduce((sum, c) => sum + c.count, 0)}
                    </span>
                  )}
                </button>
              ))}
            </div>
            <button
              className={`filter-button ${filtersOpen || activeFilters ? "selected" : ""}`}
              aria-expanded={filtersOpen}
              aria-controls="catalog-filters"
              onClick={() => setFiltersOpen(!filtersOpen)}
            >
              <SlidersHorizontal size={15} /> Filters
              {activeFilters > 0 && <span>{activeFilters}</span>}
            </button>
          </div>
          <div className="search-row">
            <label className="search-box">
              <Search size={17} />
              <input
                aria-label="Search products"
                placeholder="Find something good…"
                value={search}
                maxLength={100}
                onChange={(event) => setSearch(event.target.value)}
              />
              {search && (
                <button aria-label="Clear search" onClick={() => setSearch("")}>
                  <X size={15} />
                </button>
              )}
            </label>
            <div className="sort-control">
              <label htmlFor="sort">Sort by</label>
              <select
                id="sort"
                value={sort}
                onChange={(event) => {
                  setSort(event.target.value);
                  setPage(1);
                }}
              >
                <option value="featured">Featured</option>
                <option value="price-asc">Price: low to high</option>
                <option value="price-desc">Price: high to low</option>
                <option value="name">Name: A to Z</option>
              </select>
              <ChevronDown size={14} />
            </div>
          </div>
          {filtersOpen && (
            <div id="catalog-filters" className="filter-panel">
              <label>
                Budget
                <select
                  value={maxPrice}
                  onChange={(event) => {
                    setMaxPrice(event.target.value);
                    setPage(1);
                  }}
                >
                  <option value="all">Any price</option>
                  <option value="50">Up to $50</option>
                  <option value="100">Up to $100</option>
                  <option value="150">Up to $150</option>
                  <option value="200">Up to $200</option>
                </select>
              </label>
              <label className="checkbox-label">
                <input
                  type="checkbox"
                  checked={inStock}
                  onChange={(event) => {
                    setInStock(event.target.checked);
                    setPage(1);
                  }}
                />{" "}
                In stock only
              </label>
              <button className="text-button" onClick={clearFilters}>
                Reset filters
              </button>
            </div>
          )}
          <div className="results-meta" role="status" aria-live="polite">
            {loading
              ? "Finding your essentials…"
              : error
                ? "Collection unavailable"
                : `${data?.total ?? 0} thoughtfully chosen essentials`}
            {!loading && !error && <span>All prices in USD</span>}
          </div>

          {loading ? (
            <div
              className="product-grid"
              aria-label="Loading products"
              aria-busy="true"
            >
              {Array.from({ length: 8 }, (_, i) => (
                <div className="skeleton-card" key={i}>
                  <div />
                  <span />
                  <span />
                </div>
              ))}
            </div>
          ) : error ? (
            <div className="empty-state" role="alert">
              <Layers3 size={32} />
              <h3>A small pause.</h3>
              <p>{error}</p>
              <button
                className="button dark"
                onClick={() => setRetry(retry + 1)}
              >
                Try again <ArrowRight size={16} />
              </button>
            </div>
          ) : data?.items.length === 0 ? (
            <div className="empty-state">
              <Search size={32} />
              <h3>Nothing here just yet.</h3>
              <p>Try another search or give your filters a little more room.</p>
              <button className="button dark" onClick={clearFilters}>
                Clear filters <ArrowRight size={16} />
              </button>
            </div>
          ) : (
            <div className="product-grid">
              {data?.items.map((product) => (
                <article className="product-card" key={product.id}>
                  <button
                    className={`product-image ${product.color}`}
                    onClick={() => openProduct(product.id)}
                    aria-label={`View ${product.name}`}
                  >
                    {product.badge && (
                      <span className="product-badge">{product.badge}</span>
                    )}
                    <ProductArt kind={product.artwork} />
                    <span className="product-open">
                      <ArrowUpRight size={18} />
                    </span>
                  </button>
                  <div className="product-info">
                    <div>
                      <span className="product-category">
                        {product.category}
                      </span>
                      <h3>
                        <button onClick={() => openProduct(product.id)}>
                          {product.name}
                        </button>
                      </h3>
                      <p>
                        {product.stock > 0
                          ? "Made for your everyday"
                          : "Currently out of stock"}
                      </p>
                    </div>
                    <span className="product-price">
                      {money(product.price, product.currency)}
                    </span>
                  </div>
                </article>
              ))}
            </div>
          )}
          {!loading && !error && totalPages > 1 && (
            <nav className="pagination" aria-label="Catalog pages">
              <span>
                Page {page} of {totalPages}
              </span>
              <div>
                <button
                  aria-label="Previous page"
                  disabled={page <= 1}
                  onClick={() => setPage(page - 1)}
                >
                  <ArrowLeft size={17} />
                </button>
                {Array.from({ length: totalPages }, (_, i) => (
                  <button
                    key={i}
                    aria-label={`Page ${i + 1}`}
                    aria-current={page === i + 1 ? "page" : undefined}
                    onClick={() => setPage(i + 1)}
                  >
                    {i + 1}
                  </button>
                ))}
                <button
                  aria-label="Next page"
                  disabled={page >= totalPages}
                  onClick={() => setPage(page + 1)}
                >
                  <ArrowRight size={17} />
                </button>
              </div>
            </nav>
          )}
        </section>

        <section className="editorial shell">
          <span className="editorial-icon">
            <Sparkles size={29} strokeWidth={1.2} />
          </span>
          <div>
            <div className="eyebrow">A MORE THOUGHTFUL EVERYDAY</div>
            <h2>Less searching. More finding.</h2>
            <p>
              Great essentials don’t need to be complicated. Just right for you.
            </p>
          </div>
          <button
            className="button light"
            onClick={() => aboutDialog.current?.showModal()}
          >
            Get to know us <ArrowUpRight size={17} />
          </button>
        </section>
      </main>

      <footer className="footer shell">
        <div>
          <a href="/" className="brand">
            <span className="brand-mark">
              <Layers3 size={19} />
            </span>
            commerce<span className="brand-light">pilot</span>
          </a>
          <p>Good things for your everyday.</p>
        </div>
        <div className="footer-right">
          <span>A fictional collection. A real browsing experience.</span>
          <span>
            Demo catalog · No real payments{" "}
            <span className="footer-dot">●</span>
          </span>
        </div>
      </footer>

      <dialog
        ref={productDialog}
        className="product-dialog"
        aria-labelledby="product-dialog-title"
        onClick={(e) => {
          if (e.target === e.currentTarget) productDialog.current?.close();
        }}
        onClose={() => detailRequest.current?.abort()}
      >
        <button
          className="dialog-close"
          aria-label="Close product details"
          onClick={() => productDialog.current?.close()}
          autoFocus
        >
          <X size={20} />
        </button>
        {detailLoading ? (
          <div className="dialog-loading" role="status">
            <h2 id="product-dialog-title">Product details</h2>
            <LoaderCircle className="spin" /> Loading product…
          </div>
        ) : detailError ? (
          <div className="dialog-loading" role="alert">
            <h2 id="product-dialog-title">Product unavailable</h2>
            <p>
              We couldn’t load this product. Close this window and try again.
            </p>
          </div>
        ) : (
          detail && (
            <div className="detail-layout">
              <div className={`detail-art ${detail.color}`}>
                <ProductArt kind={detail.artwork} />
              </div>
              <div className="detail-copy">
                <span className="eyebrow">{detail.category}</span>
                <h2 id="product-dialog-title">{detail.name}</h2>
                <strong className="detail-price">
                  {money(detail.price, detail.currency)}{" "}
                  <small>{detail.currency}</small>
                </strong>
                <p>{detail.description}</p>
                <span
                  className={`stock-status ${detail.stock ? "" : "unavailable"}`}
                >
                  {detail.stock > 0 && <Check size={15} />}
                  {detail.stock > 0
                    ? `In stock · ${detail.stock} available`
                    : "Currently out of stock"}
                </span>
                <AddToBag product={detail} />
                <div className="detail-notice">
                  Your bag is saved to your account. This is a demo collection;
                  checkout is not available yet.
                </div>
              </div>
            </div>
          )
        )}
      </dialog>
      <dialog
        ref={aboutDialog}
        className="about-dialog"
        aria-labelledby="about-title"
        onClick={(e) => {
          if (e.target === e.currentTarget) aboutDialog.current?.close();
        }}
      >
        <button
          className="dialog-close"
          aria-label="Close about"
          onClick={() => aboutDialog.current?.close()}
          autoFocus
        >
          <X size={20} />
        </button>
        <span className="brand-mark">
          <Layers3 size={24} />
        </span>
        <div className="eyebrow">HELLO, WE’RE COMMERCEPILOT</div>
        <h2 id="about-title">
          A little more
          <br />
          considered.
        </h2>
        <p>
          We believe the things you use every day should make life feel simpler.
          That’s the idea behind this collection: useful tech, quiet design, and
          room for what matters.
        </p>
        <p className="about-note">
          CommercePilot is an interactive commerce demo. Products,
          specifications, prices, and stock are fictional. This preview lets you
          explore the catalog and save a personal bag. Checkout and the AI
          shopping assistant are not yet enabled.
        </p>
        <button
          className="button dark"
          onClick={() => {
            aboutDialog.current?.close();
            explore();
          }}
        >
          Find your everyday <ArrowUpRight size={17} />
        </button>
      </dialog>
    </>
  );
}
