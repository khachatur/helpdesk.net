const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** A date and time in the viewer's locale and time zone, such as "13 Nov 2026, 14:05". */
export function formatDateTime(iso: string): string {
  return dateTime.format(new Date(iso))
}
