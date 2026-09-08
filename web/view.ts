export function element<T extends HTMLElement = HTMLElement>(id: string): T {
  const found = document.getElementById(id);
  if (!found) throw new Error(`Missing interface element: ${id}`);
  return found as T;
}

export function message(text: string, error = false): void {
  const output = element('status');
  output.textContent = text;
  output.classList.toggle('error', error);
  output.setAttribute('role', error ? 'alert' : 'status');
}

export function button(text: string, action: () => Promise<void>): HTMLButtonElement {
  const control = document.createElement('button');
  control.type = 'button';
  control.textContent = text;
  control.addEventListener('click', () => { void run(control, action); });
  return control;
}

const busyControls = new WeakSet<HTMLButtonElement>();
let pendingActions = 0;

export async function run(control: HTMLButtonElement | null, action: () => Promise<void>): Promise<void> {
  if (control && busyControls.has(control)) return;
  if (control) { busyControls.add(control); control.setAttribute('aria-disabled', 'true'); }
  pendingActions++;
  element('workspace').setAttribute('aria-busy', 'true');
  try { await action(); }
  catch (error: unknown) {
    if (!(error instanceof DOMException && error.name === 'AbortError')) message(error instanceof Error ? error.message : 'The operation failed.', true);
  }
  finally {
    if (control) { busyControls.delete(control); control.removeAttribute('aria-disabled'); }
    pendingActions--;
    element('workspace').setAttribute('aria-busy', pendingActions > 0 ? 'true' : 'false');
  }
}

export function bind(id: string, action: () => Promise<void>): void {
  const control = element<HTMLButtonElement>(id);
  control.addEventListener('click', () => { void run(control, action); });
}

export function line(parent: HTMLElement, text: string, className = ''): HTMLElement {
  const result = document.createElement(['UL', 'OL'].includes(parent.tagName) ? 'li' : 'p');
  result.textContent = text;
  result.className = className;
  parent.append(result);
  return result;
}

export function link(parent: HTMLElement, title: string, target: string, external = false): void {
  const url = new URL(target, location.origin);
  if (external ? url.protocol !== 'https:' || !['last.fm', 'www.last.fm'].includes(url.hostname)
    : url.origin !== location.origin) return;
  const result = document.createElement('a');
  result.textContent = title;
  result.href = url.href;
  if (external) { result.target = '_blank'; result.rel = 'noopener noreferrer'; }
  parent.append(result);
}
