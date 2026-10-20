# Language resources

`en.json` owns the resource keys and composite-format contract. Each supported
language must contain the same unique keys, nonempty text and unchanged format
arguments such as `{0}` and `{1:N0}`. Preserve filesystem expressions and product
names when translating; review confirmations and destructive-action descriptions
for their full meaning.

Add new text to every language file in the same change. Keep UTF-8 characters
literal and use typographic quotes inside values. Do not copy English prose into
another language to satisfy key coverage.

Run the Localization filter in the [test guide](../../../../../tests/README.md#domain-filters).

These checks cover key references, key parity, values, enum-derived labels and
composite-format arguments. Validate visual layout through the native UI hosts
when a translation changes control sizing or text direction.
