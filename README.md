# Comic POS

A point-of-sale system for a comic book store: inventory, checkout, customer
pull lists, automatic discovery of new releases from Comic Vine, FOC (Final
Order Cutoff) buy reports with automatic email alerts, sales history, a
dashboard, and database backups.

## Running it

1. Open `ComicPos.slnx` in Visual Studio, or from a terminal in this folder:
   ```
   dotnet build ComicPos.slnx
   dotnet run --project ComicPos.Web
   ```
   then open the address it prints in a browser. `ComicPos.Web` (Blazor) is
   the actively developed app. `ComicPos.App` (WinForms) still builds and
   runs against the same database, but gets no new features.
2. That's it. The database is created automatically the first time the app
   runs (in `%LOCALAPPDATA%\ComicPos\comic_pos.db`), and any future schema
   updates are applied automatically on startup too — there's nothing to
   run by hand.
3. First login: on a brand-new (or reset) database the app creates one
   worker, **Admin**, PIN **9876**. Change that PIN right away in
   **Settings → Workers → Change PIN**, then add your real staff.

## Day one: getting comics into the system

You don't need a spreadsheet to get started. Go to the **Inventory** tab and
click **+ Add New Item** to add a single comic, graphic novel, or back
issue by hand: series name, issue number, barcode, price, and starting
stock.

If you get a weekly solicitation list from your distributor as a CSV, use
the **Import** tab instead to add or receive many items at once. See
`sample-solicitation.csv` in this folder for the expected column layout.
Re-importing a file you've already run updates existing items (price
changes, received quantities) instead of duplicating them.

When a box actually arrives, you don't have to retype anything: in
**Inventory**, click **+ Add New Item**, scan (or type) the barcode, and
the item's details auto-fill from whatever Comic Vine data the app already
has for it (from a prior New Books check or CSV import) — just confirm the
price and stock quantity and save.

## Daily use

- **Checkout** — scan a barcode (or type it and press Enter) to add an item.
  Edit quantity/price/discount directly in the cart grid, or select a row
  and click **Remove Selected Item** / press Delete to take it out. Sales
  tax comes from the rate set in **Settings**. After completing a sale you
  can print the receipt to any Windows printer.
- **Customers** — search or add customers, view their purchase history,
  manage their pull list (the comics they get set aside every week), and see
  their open **holds** (one-off copies set aside) with Picked up / Cancel.
- **Holds** — from an issue's screen in **Inventory**, click **Hold** to set
  a copy aside for a customer. Ringing that customer up for the same issue
  and cover at Checkout closes the hold automatically; voiding that sale
  reopens it.
- **History** — browse all past sales by series/issue. Made a mistake on a
  sale? Open it and click **Void Sale** — it restores the stock and marks
  the sale voided (it's kept for the record, not deleted).
- **Dashboard** — today/week/month revenue, top sellers, and low-stock
  items at a glance.
- **New Books** — pulls solicitations straight from the two distributors,
  with no files to download by hand:
  - **Lunar Distribution** (DC and others): **Fetch Lunar Release** for
    this week, or **Backfill All Lunar History** for everything back to
    Lunar's 2020 launch. Cover images come embedded in Lunar's files.
  - **Penguin Random House** (Marvel, IDW and others): **Fetch PRH
    Catalog** for one month, or **Fetch All Available PRH Catalogs**.
    Each import also reads PRH's own book pages for variant covers
    (Variant B, RI 1:10 incentives, artist variants) and pulls cover art
    from PRH's image server.
  - **Fetch Everything** runs all of the above in order. It can take
    hours; a Wi-Fi drop stops it cleanly and says what to re-run.

  Everything found is added to inventory as not-yet-received, zero-stock
  items, ready for the receiving workflow above. The older weekly Comic
  Vine / Metron checks are still there as fallbacks.
- **FOC Report** — before each Final Order Cutoff date, open this tab,
  generate the report, and either read it on screen or click **Send Email
  to Admin** to email a subscriber-by-subscriber buy list (set up the SMTP
  details in **Settings** first — works with Gmail using an App Password).
  You can also turn on **Auto-email FOC report daily** in Settings so this
  gets sent to the admin on its own every day, with nothing to click.

## Settings

Set your store name and sales tax rate, your email settings for the FOC
report (including the daily auto-send option above), your Comic Vine API
key and the publishers you want New Books to watch (comma-separated,
spelled exactly as Comic Vine has them, e.g. `Marvel, DC Comics, Image`),
and you can trigger a one-click database backup (saved to
`%LOCALAPPDATA%\ComicPos\Backups\`) any time. It's a good habit to do this
every so often, or before any major data import.

## Your data

Everything lives locally in `%LOCALAPPDATA%\ComicPos\` on this computer:
the database, email/store settings, and backups. None of it is committed to
this code repository (`comic_pos.db` is in `.gitignore`), so it's safe to
share or back up this project's code without exposing store data.
