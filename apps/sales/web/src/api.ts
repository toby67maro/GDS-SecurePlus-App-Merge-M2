export class ApiError extends Error {
  constructor(
    message: string,
    readonly fieldErrors: Record<string, string[]> = {},
    readonly status?: number,
  ) {
    super(message);
  }
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      ...init,
      credentials: "same-origin",
      headers: {
        Accept: "application/json",
        ...(init?.body ? { "Content-Type": "application/json" } : {}),
        ...init?.headers,
      },
    });
  } catch (error) {
    if (init?.signal?.aborted) throw error;
    throw new ApiError("The local API is unavailable. Start the API server and try again.");
  }

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as
      | { title?: string; message?: string; errors?: Record<string, string[]> }
      | null;
    const details = body?.errors
      ? Object.values(body.errors).flat().join(" ")
      : body?.message;
    throw new ApiError(
      details || body?.title || `The request failed (${response.status}).`,
      body?.errors ?? {},
      response.status,
    );
  }

  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}
