import http from "k6/http";
import { check, sleep } from "k6";

// ---------------------------------------------------------------------------
// k6 Load Test — "Customer searches and views a product"
//
// Stress-tests ALL parts of the catalog flow to generate meaningful
// observability data (errors, cache pressure, latency spikes):
//   1. Opens the homepage
//   2. Searches for a product keyword (Search)
//   3. Autocomplete / search suggestions (Search)
//   4. Views product detail pages (Catalogue + Pricing)
//   5. Browses category pages (Catalogue + Search)
//   6. Browses manufacturer pages (Catalogue + Search)
//   7. Hits invalid/non-existent URLs to generate errors
//   8. Rapid-fire searches with unique terms to force cache misses
//
// Usage:
//   k6 run observability/loadtest/search-flow.js
//   k6 run --vus 100 --duration 5m observability/loadtest/search-flow.js
// ---------------------------------------------------------------------------

const BASE_URL = __ENV.BASE_URL || "http://localhost:5050";

// Search terms that match nopCommerce sample data
const SEARCH_TERMS = [
  "computer",
  "laptop",
  "phone",
  "camera",
  "book",
  "digital",
  "apple",
  "nike",
  "gift",
  "software",
  "jeans",
  "jewelry",
  "shoes",
  "tablet",
];

// Terms that will NOT match anything — force empty results + cache misses
const GARBAGE_SEARCH_TERMS = [
  "xyznonexistent",
  "qwertyasdf",
  "zzz_no_match_111",
  "fakebrand_loadtest",
  "randomproduct99999",
  "aaabbbcccdddeee",
  "loadtest_cache_miss_001",
  "loadtest_cache_miss_002",
  "loadtest_cache_miss_003",
  "nonexistent_category_xyz",
  "undefined",
  "null",
  "NaN",
  "verylongsearchterm_that_no_product_will_ever_match_in_the_database",
];

// Product slugs from nopCommerce sample data
const PRODUCT_SLUGS = [
  "build-your-own-computer",
  "digital-storm-vanquish-custom-performance-pc",
  "lenovo-ideacentre",
  "apple-macbook-pro",
  "asus-n551jk-xo076h-laptop",
  "samsung-series-9-702a3b",
  "hp-spectre-xt-pro-ultrabook",
  "hp-envy-15-as004nia",
  "lenovo-thinkpad-x201-tablet",
  "obey-propaganda-hat",
  "nikon-d5500-dslr",
  "leica-t-mirrorless-digital-camera",
  "adidas-consortium-campus-80s-running-shoes",
  "first-prize-pies",
  "fahrenheit-451-by-ray-bradbury",
];

// Slugs that do NOT exist — will trigger 404s / errors in the catalog flow
const INVALID_PRODUCT_SLUGS = [
  "this-product-does-not-exist-12345",
  "fake-laptop-xyz",
  "nonexistent-camera-model",
  "deleted-product-old",
  "test-invalid-slug-000",
];

// Category slugs from nopCommerce sample data
const CATEGORY_SLUGS = [
  "computers",
  "desktops",
  "notebooks",
  "software",
  "electronics",
  "camera-photo",
  "cell-phones",
  "apparel",
  "shoes",
  "clothing",
  "accessories",
  "digital-downloads",
  "books",
  "jewelry",
  "gift-cards",
];

// Manufacturer slugs
const MANUFACTURER_SLUGS = [
  "apple",
  "hp",
  "nike",
];

// ---------- test configuration ----------

export const options = {
  stages: [
    { duration: "20s", target: 50 },    // ramp up aggressively
    { duration: "20s", target: 120 },   // push hard
    { duration: "1m",  target: 150 },   // sustained heavy load
    { duration: "20s", target: 250 },   // extreme spike
    { duration: "30s", target: 250 },   // hold the spike — stress everything
    { duration: "20s", target: 150 },   // partial recovery
    { duration: "30s", target: 150 },   // sustained again
    { duration: "20s", target: 50 },    // ramp down
    { duration: "20s", target: 0 },     // cool down
  ],
  thresholds: {
    http_req_duration: ["p(95)<8000"],   // 95% under 8s (expect some slow ones)
    http_req_failed: ["rate<0.30"],      // up to 30% errors allowed (we intentionally cause some)
  },
};

// ---------- helpers ----------

function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}

// ---------- user journey ----------

export default function () {
  // Step 1: Homepage
  const homeRes = http.get(`${BASE_URL}/`, { tags: { step: "homepage" } });
  check(homeRes, { "homepage 200": (r) => r.status === 200 });
  sleep(Math.random() * 0.5 + 0.2); // very short think time — aggressive

  // Step 2: Search for a product (Search sub-flow)
  const term = pick(SEARCH_TERMS);
  const searchRes = http.get(`${BASE_URL}/search?q=${term}`, {
    tags: { step: "search" },
  });
  check(searchRes, { "search 200": (r) => r.status === 200 });
  sleep(Math.random() * 0.3 + 0.1);

  // Step 3: Autocomplete — rapid typing simulation (Search sub-flow)
  const autoTerm = term.substring(0, 3);
  const autoRes = http.get(
    `${BASE_URL}/catalog/searchtermautocomplete?term=${autoTerm}`,
    { tags: { step: "autocomplete" } }
  );
  check(autoRes, { "autocomplete 200": (r) => r.status === 200 });
  sleep(Math.random() * 0.2 + 0.1);

  // Step 4: View a product detail page (Catalogue + Pricing sub-flows)
  const slug = pick(PRODUCT_SLUGS);
  const productRes = http.get(`${BASE_URL}/${slug}`, {
    tags: { step: "product_detail" },
  });
  check(productRes, {
    "product ok": (r) => r.status === 200 || r.status === 301 || r.status === 302,
  });
  sleep(Math.random() * 0.5 + 0.3);

  // Step 5: View a second product — compare products (Catalogue + Pricing)
  const slug2 = pick(PRODUCT_SLUGS);
  const product2Res = http.get(`${BASE_URL}/${slug2}`, {
    tags: { step: "product_detail" },
  });
  check(product2Res, {
    "product2 ok": (r) => r.status === 200 || r.status === 301 || r.status === 302,
  });
  sleep(Math.random() * 0.3 + 0.1);

  // Step 6: View a THIRD product — heavy browsing session (Catalogue + Pricing)
  const slug3 = pick(PRODUCT_SLUGS);
  http.get(`${BASE_URL}/${slug3}`, { tags: { step: "product_detail" } });
  sleep(Math.random() * 0.3 + 0.1);

  // Step 7: Browse a category (Catalogue + Search sub-flows)
  const category = pick(CATEGORY_SLUGS);
  const categoryRes = http.get(`${BASE_URL}/${category}`, {
    tags: { step: "category" },
  });
  check(categoryRes, {
    "category ok": (r) => r.status === 200 || r.status === 301 || r.status === 302,
  });
  sleep(Math.random() * 0.3 + 0.1);

  // Step 8: Browse a second category
  const category2 = pick(CATEGORY_SLUGS);
  http.get(`${BASE_URL}/${category2}`, { tags: { step: "category" } });
  sleep(Math.random() * 0.2 + 0.1);

  // Step 9: Browse a manufacturer page (Catalogue + Search sub-flows)
  const manufacturer = pick(MANUFACTURER_SLUGS);
  const mfgRes = http.get(`${BASE_URL}/${manufacturer}`, {
    tags: { step: "manufacturer" },
  });
  check(mfgRes, {
    "manufacturer ok": (r) => r.status === 200 || r.status === 301 || r.status === 302,
  });
  sleep(Math.random() * 0.2 + 0.1);

  // Step 10: Search with a garbage/unique term — forces cache MISSES
  const garbageTerm = pick(GARBAGE_SEARCH_TERMS);
  const garbageRes = http.get(`${BASE_URL}/search?q=${encodeURIComponent(garbageTerm)}`, {
    tags: { step: "search_garbage" },
  });
  check(garbageRes, {
    "garbage search responded": (r) => r.status < 500,
  });
  sleep(Math.random() * 0.2 + 0.1);

  // Step 11: Try to view a product that does NOT exist — triggers errors/404s
  const invalidSlug = pick(INVALID_PRODUCT_SLUGS);
  const invalidRes = http.get(`${BASE_URL}/${invalidSlug}`, {
    tags: { step: "invalid_product" },
  });
  check(invalidRes, {
    "invalid product handled": (r) => r.status === 404 || r.status === 302 || r.status === 200,
  });
  sleep(Math.random() * 0.2 + 0.1);

  // Step 12: Autocomplete with garbage — more cache misses
  const garbageAuto = pick(GARBAGE_SEARCH_TERMS).substring(0, 5);
  http.get(
    `${BASE_URL}/catalog/searchtermautocomplete?term=${encodeURIComponent(garbageAuto)}`,
    { tags: { step: "autocomplete_garbage" } }
  );
  sleep(Math.random() * 0.1 + 0.05);

  // Step 13: Rapid-fire search burst — 3 searches back-to-back with no pause
  // This hammers the Search + Catalogue sub-flows and stresses the cache
  for (let i = 0; i < 3; i++) {
    const burstTerm = pick(SEARCH_TERMS) + "_" + Math.floor(Math.random() * 10000);
    http.get(`${BASE_URL}/search?q=${encodeURIComponent(burstTerm)}`, {
      tags: { step: "search_burst" },
    });
  }
  sleep(Math.random() * 0.2 + 0.1);

  // Step 14: One final product view (Catalogue + Pricing)
  const finalSlug = pick(PRODUCT_SLUGS);
  http.get(`${BASE_URL}/${finalSlug}`, { tags: { step: "product_detail" } });
  sleep(Math.random() * 0.3 + 0.1);

  // Step 15: Final search — refining (Search sub-flow)
  const term2 = pick(SEARCH_TERMS);
  const search2Res = http.get(`${BASE_URL}/search?q=${term2}`, {
    tags: { step: "search" },
  });
  check(search2Res, { "search2 200": (r) => r.status === 200 });
}
