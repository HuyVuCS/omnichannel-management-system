// k6 script of the reserve path (task 026b, Part E): orders submitted through the API as a channel would
// (POST /api/orders/{channel}); what the Order Worker makes of them is judged afterwards by scripts/load/run.ps1.
// Run by run.ps1 only (it prepares the customer, the products and the token); never against production.
//
// Environment: BASE_URL (the API origin as the k6 container reaches it), TOKEN (an access token; never printed),
// SCENARIO (contention | spread | duplicates), RUN_ID (part of every external order id), CUSTOMER_ID,
// PRODUCT_IDS (comma-separated), CHANNEL (default website).
import http from 'k6/http';
import exec from 'k6/execution';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL;
const SCENARIO = __ENV.SCENARIO;
const RUN_ID = __ENV.RUN_ID;
const CHANNEL = __ENV.CHANNEL || 'website';
const CUSTOMER_ID = __ENV.CUSTOMER_ID;
const PRODUCT_IDS = (__ENV.PRODUCT_IDS || '').split(',').filter((id) => id);

const SCENARIOS = {
  // One product with a stock of 100 and 300 orders of one piece, as fast as the intake accepts them.
  contention: { executor: 'shared-iterations', vus: 30, iterations: 300, maxDuration: '5m' },
  // 2,000 orders at a constant 20 per second for 100 s, one to three lines over 50 products.
  spread: { executor: 'constant-arrival-rate', rate: 20, timeUnit: '1s', duration: '100s', preAllocatedVUs: 40, maxVUs: 200 },
  // 100 external order ids, each submitted twice at the same moment.
  duplicates: { executor: 'shared-iterations', vus: 20, iterations: 100, maxDuration: '5m' },
};

export const options = {
  scenarios: { [SCENARIO]: SCENARIOS[SCENARIO] },
  summaryTrendStats: ['min', 'med', 'p(95)', 'p(99)', 'max', 'avg'],
};

// One counter per answer of the intake that can occur; anything else is "other".
const STATUSES = [202, 400, 401, 404, 409, 413, 429, 500, 502, 503];
const counters = {};
for (const status of STATUSES) {
  counters[status] = new Counter(`intake_status_${status}`);
}
const otherStatus = new Counter('intake_status_other');

function count(response) {
  (counters[response.status] || otherStatus).add(1);
}

function order(externalOrderId, items) {
  return {
    method: 'POST',
    url: `${BASE_URL}/api/orders/${CHANNEL}`,
    body: JSON.stringify({ externalOrderId, customerId: CUSTOMER_ID, items }),
    params: { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${__ENV.TOKEN}` } },
  };
}

function submit(request) {
  count(http.post(request.url, request.body, request.params));
}

// One to three different products, one or two pieces each - the same for a given order number.
function spreadItems(n) {
  const lines = 1 + (n % 3);
  const items = [];
  for (let line = 0; line < lines; line++) {
    const product = PRODUCT_IDS[(n * 7 + line * 13) % PRODUCT_IDS.length];
    if (!items.some((item) => item.productId === product)) {
      items.push({ productId: product, stock: 1 + ((n + line) % 2) });
    }
  }
  return items;
}

export default function () {
  const n = exec.scenario.iterationInTest;
  const externalOrderId = `LOAD-${RUN_ID}-${SCENARIO}-${n}`;

  if (SCENARIO === 'contention') {
    submit(order(externalOrderId, [{ productId: PRODUCT_IDS[0], stock: 1 }]));
  } else if (SCENARIO === 'spread') {
    submit(order(externalOrderId, spreadItems(n)));
  } else {
    // The same order twice, in parallel.
    const request = order(externalOrderId, [{ productId: PRODUCT_IDS[n % PRODUCT_IDS.length], stock: 1 }]);
    for (const response of http.batch([request, request])) {
      count(response);
    }
  }
}

// One JSON line for run.ps1 instead of the text summary.
export function handleSummary(data) {
  const metric = (name) => (data.metrics[name] ? data.metrics[name].values : {});
  const statuses = {};
  for (const status of STATUSES) {
    statuses[status] = metric(`intake_status_${status}`).count || 0;
  }
  statuses.other = metric('intake_status_other').count || 0;
  const duration = metric('http_req_duration');
  return {
    stdout: JSON.stringify({
      scenario: SCENARIO,
      requests: metric('http_reqs').count || 0,
      requestsPerSecond: metric('http_reqs').rate || 0,
      iterations: metric('iterations').count || 0,
      droppedIterations: metric('dropped_iterations').count || 0,
      failedRate: metric('http_req_failed').rate || 0,
      durationMs: { p50: duration.med, p95: duration['p(95)'], p99: duration['p(99)'], max: duration.max },
      statuses,
      testSeconds: data.state.testRunDurationMs / 1000,
    }),
  };
}
