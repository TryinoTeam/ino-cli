# POS file format package: what the engine allows

The package only says how your tills' file maps to Ino POS file v1: which column is which Ino field, which values mean a sale line, a payment or a
return line, and how dates and amounts are written. It makes no calls. Duplicate checks, the order of sales and member checks stay in the engine and
are the same for every format; the same sale sent in v1 and in your format gets the same ids.

- **Id:** `local_<name>` (lower case, digits, `-`). The `category` is `POS` and the file has a `posFile` block only.
- **columns:** Ino field = your column. The Ino fields are `RecordType`, `OrderNumber`, `ReturnNumber`, `LineNumber`, `StoreCode`, `TillId`,
  `CustomerId`, `OperatorId`, `Sku`, `ProductName`, `Quantity`, `UnitPrice`, `OriginalUnitPrice`, `PaymentType`, `PaymentAmount`, `TransactionDate`,
  `CouponCode`. A return line needs `ReturnNumber`. `CustomerId` (the member) and `OperatorId` (the cashier) are personal data and are shown as such to
  the person who pins the format.
- **recordTypes:** your values for `SALE_LINE`, `PAYMENT`, `RETURN_LINE` (or `recordType` when every row is one kind).
- **paymentTypes:** your value → Ino payment type.
- **amountScale:** `1` when amounts are written as 12.50, `100` when they are in cents (1250). Thousands separators are not read.
- **dateFormat:** for example `dd.MM.yyyy HH:mm`; `culture` (e.g. `tr-TR`) for month names; `encoding` when the file is not UTF-8.

Not supported: payments as columns of each sale line, returns as negative quantities, files without a header row, fixed-width files.

After `ino connector package save connector.json`, a person with the Connector Installer role pins it to the channel in Power Apps
(Integrations → Channels → the channel → POS file format) after reviewing the column summary.
