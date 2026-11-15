const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

const shortDateTime = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })

/** A compact date and time for table cells, such as "13 Nov, 14:05". The year is left out. */
export function formatShortDateTime(iso: string): string {
  return shortDateTime.format(new Date(iso))
}

/** A date and time in the viewer's locale and time zone, such as "13 Nov 2026, 14:05". */
export function formatDateTime(iso: string): string {
  return dateTime.format(new Date(iso))
}
