# SOP-101 Scanning material in and out of the yard

| | |
|---|---|
| Applies to | Yard operators, shipping office, drivers |
| Equipment | Wireless handheld (HH-xx) or scanning kiosk running YardTracker Station |
| Owner | Yard supervisor |

## 1. Before the shift

1. Collect a charged handheld and sign in by scanning your badge.
2. Confirm the header shows **Online**. If it shows **Offline**, you may keep scanning. Scans are stored on the device and sync automatically, but tell the supervisor if it lasts more than 15 minutes.
3. Check that the device clock matches the office clock. Scans more than 5 minutes ahead are rejected as `ClockSkew`.

## 2. Receiving material (Receive)

1. Verify the mill test report (MTR) heat number against the bundle stencil.
2. Apply a tag to the bundle and select **Receive**.
3. Choose the product, enter the heat number and piece count, and choose the receiving dock.
4. Scan the tag. A green banner confirms the receipt.
5. If the heat number does not match the MTR, move the bundle to the QA hold location (for example `NYD-QA`) and notify QA.

## 3. Moving material (Move)

1. Select **Move** and scan the rack or laydown label (`LOC:...`) to set the target location.
2. Scan each bundle as it is set down, not when it is picked up.
3. Moves between sites are not allowed from the handheld. Load the truck using the delivery flow.

## 4. Issuing material (Check out)

1. Select **Check out** and enter the work order or BOL number.
2. Scan each bundle as it leaves the location.
3. A red banner means the scan was rejected. Do not ship the bundle until the reason is resolved.

## 5. Returns (Check in)

1. Select **Check in**, choose the return location, and enter the return reference.
2. Scan each bundle.

## 6. Rejected scans

Every rejected scan is logged and appears in the daily **Scan Exception Report** in this SharePoint site.

| Banner | Meaning | What to do |
|---|---|---|
| Unknown tag | Tag not registered or misread | Rescan. If it repeats, receive the item or re-tag it |
| Not allowed | Item is in the wrong state (already checked out, in transit) | Look it up and ask the supervisor |
| Wrong site | Location belongs to another site | Use a truck delivery |
| Already recorded | The handheld retried a scan | No action needed |
