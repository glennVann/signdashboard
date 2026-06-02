export const API_BASE_URL =
  process.env.NEXT_PUBLIC_API_BASE_URL ?? "http://localhost:5206/api";

export const SIGNALR_HUB_URL = API_BASE_URL.replace(/\/api\/?$/, "/hubs/dashboard");
export const AGENT_DOWNLOAD_URL = `${API_BASE_URL}/agent/download`;

export async function apiGet(path) {
  const response = await fetch(`${API_BASE_URL}${path}`, { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`GET ${path} failed`);
  }

  return response.json();
}

export async function apiSend(path, body, method = "POST") {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `${method} ${path} failed`));
  }

  return response.json();
}

export async function apiUpload(path, formData) {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: "POST",
    body: formData
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `UPLOAD ${path} failed`));
  }

  return response.json();
}

async function readErrorMessage(response, fallback) {
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("application/json")) {
    const body = await response.json().catch(() => null);
    return body?.detail ?? body?.title ?? body?.message ?? fallback;
  }

  const text = await response.text().catch(() => "");
  return text || fallback;
}
