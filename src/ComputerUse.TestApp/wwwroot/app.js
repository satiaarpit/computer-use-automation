const parameters = new URLSearchParams(window.location.search);
const state = parameters.get("state") || "success";
const query = parameters.get("query") || "Aurora";
const searchView = document.querySelector("#search-view");
const reviewView = document.querySelector("#review-view");
const status = document.querySelector("#status");
const results = document.querySelector("#results");
const queryInput = document.querySelector("#query");
const stateBadge = document.querySelector("#state-badge");
const dialog = document.querySelector("#fixture-dialog");
const redirectEscape = document.querySelector("#redirect-escape");

stateBadge.textContent = state;
queryInput.value = query;

if (window.location.pathname === "/review") {
  searchView.hidden = true;
  reviewView.hidden = false;
  fetch("/api/session")
    .then(response => response.json())
    .then(payload => { document.querySelector("#session-id").textContent = payload.sessionId; });
}

function formatRecord(record) {
  const article = document.createElement("article");
  article.className = "result-card";
  article.setAttribute("data-record-id", record.id);
  article.innerHTML = `
    <div>
      <p class="record-id">${record.id}</p>
      <h3>${record.title}</h3>
    </div>
    <dl>
      <div><dt>Price</dt><dd data-field="price">${record.price.toFixed(2)}</dd></div>
      <div><dt>Available</dt><dd data-field="available">${record.available}</dd></div>
      <div><dt>Published</dt><dd data-field="publishedOn">${record.publishedOn}</dd></div>
    </dl>`;
  return article;
}

async function search() {
  status.textContent = state === "slow-load" ? "Loading delayed results" : "Loading results";
  results.replaceChildren();

  try {
    const response = await fetch(`/api/search?query=${encodeURIComponent(queryInput.value)}&state=${encodeURIComponent(state)}`);
    const payload = await response.json();

    if (response.status === 403) {
      status.textContent = "Permission required";
      results.innerHTML = '<div class="outcome" data-outcome="permission-required"><h3>Permission required</h3><p>This scenario intentionally denies access.</p></div>';
      return;
    }

    if (payload.records.length === 0) {
      status.textContent = "No results";
      results.innerHTML = '<div class="outcome" data-outcome="no-results"><h3>No matching records</h3><p>Try a different synthetic query.</p></div>';
      return;
    }

    const fragment = document.createDocumentFragment();
    payload.records.forEach(record => fragment.appendChild(formatRecord(record)));
    results.appendChild(fragment);
    status.textContent = `${payload.records.length} result${payload.records.length === 1 ? "" : "s"}`;
  } catch {
    status.textContent = "Fixture request failed";
    results.innerHTML = '<div class="outcome" data-outcome="hard-failure"><h3>Request failed</h3></div>';
  }
}

document.querySelector("#search-form").addEventListener("submit", event => {
  event.preventDefault();
  search();
});

document.querySelector("#review-form").addEventListener("submit", async event => {
  event.preventDefault();
  const submitStatus = document.querySelector("#submit-status");
  const response = await fetch("/api/submit", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      reference: document.querySelector("#reference").value,
      note: document.querySelector("#note").value
    })
  });
  const payload = await response.json();
  submitStatus.textContent = payload.message;
  submitStatus.dataset.outcome = payload.status;
});

document.querySelector("#dismiss-dialog").addEventListener("click", () => dialog.close());

const escapedOrigin = `${window.location.protocol}//localhost:${window.location.port}`;
redirectEscape.addEventListener("click", () => {
  window.location.href = `${escapedOrigin}/?state=success`;
});

if (state === "redirect-escape") {
  redirectEscape.hidden = false;
}

if (state === "dialog") {
  dialog.showModal();
}

if (!searchView.hidden) {
  search();
}
