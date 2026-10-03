# SDS200_ControlApp Plan

## Goal

Create a Windows desktop application named `SDS200_ControlApp` for controlling and monitoring Uniden SDS200 hardware over its USB serial-port interface.

## Assumptions

- Target platform: Windows.
- UI framework: WPF.
- Target framework: .NET 8.
- SDS200 is the only supported scanner model for the first release.
- The scanner is connected through USB Serial Port mode.
- Commands are ASCII, comma-delimited, and terminated with carriage return (`\r`).
- XML responses such as `GSI`, `PSI`, and `GLT` are parsed separately from comma-delimited responses.
- The local reference library is `D:\OneDrive\Uniden Scanner`.
- The Remote Command Specification V2.00 dated 2025-07-07 remains the primary authority for command syntax.

## Proposed Solution Structure

```text
SDS200_ControlApp/
|-- SDS200_ControlApp.slnx
|-- src/
|   |-- SDS200_ControlApp/             # WPF application and views
|   |-- SDS200_ControlApp.Domain/      # Models and service contracts
|   |-- SDS200_ControlApp.Protocol/    # Command builders and response parsers
|   `-- SDS200_ControlApp.Serial/      # COM transport and connection handling
`-- tests/
    |-- SDS200_ControlApp.Protocol.Tests/
    `-- SDS200_ControlApp.Serial.Tests/
```

## Phase 1: Connection MVP

- Discover available COM ports and allow manual selection.
- Provide configurable serial settings.
- Implement connect and disconnect lifecycle management.
- Serialize command writes so concurrent operations cannot corrupt the stream.
- Read the serial stream incrementally and split responses on `\r`.
- Send `MDL\r` during connection and require `MDL,SDS200`.
- Send `VER\r` and display the scanner firmware version.
- Show connection state and basic communication errors in the UI.

## Phase 2: Scanner Status

- Implement `GSI` XML parsing.
- Implement `PSI` periodic status updates.
- Maintain a scanner state model containing:
  - Current mode
  - Frequency and modulation
  - Current system, department, site, and channel
  - Signal and activity state
  - Firmware version
  - Database counter
- Handle timeouts, malformed responses, and scanner disconnects.

## Phase 3: Control Surface

- Add scan start and stop controls using verified `KEY` or `JPM` commands.
- Add hold, next, previous, and jump controls.
- Add SDS200 volume control using the verified `0-29` range.
- Add SDS200 squelch control using the verified `0-19` range.
- Display and control Favorites List, System, and Department Quick Keys.
- Add explicit confirmation before sending `POF`.

## Phase 4: Favorites and Monitoring

- Retrieve Favorites Lists through `GLT`.
- Display systems, departments, sites, frequencies, and talkgroups.
- Treat GLT indexes as runtime handles and invalidate them when `DB_Counter` changes.
- Add temporary, permanent, and remove-avoid controls where supported.
- Consider recording controls through `URC` after the core workflow is stable.

## Debug Logging

- Add a dedicated `ILogger` abstraction shared by the UI, serial transport, and protocol layers.
- Support `Error`, `Warning`, `Info`, `Debug`, and `Trace` levels.
- Record:
  - COM port open and close events
  - Active connection settings
  - Raw commands sent
  - Raw responses received
  - Parsed protocol events
  - XML parsing failures
  - Timeouts and disconnects
  - Rejected model identifiers
- Include timestamps, direction, command name, and response elapsed time.
- Provide an in-app log viewer with filtering and copy/export support.
- Support optional rolling log files under the application data directory.
- Allow raw serial logging to be enabled independently for troubleshooting.
- Avoid logging sensitive or unnecessary data where possible.

## Protocol and Reliability Rules

- Keep transport, protocol parsing, domain state, and UI concerns independent.
- Never assume a complete response is available from one serial read.
- Use a buffered reader with carriage-return framing.
- Keep XML and comma-delimited response parsing separate.
- Prevent simultaneous command writes.
- Do not send destructive commands during startup.
- Reject non-SDS200 devices during the handshake.
- Do not invent baud, parity, or handshake settings; verify them against the SDS200 documentation or a known working connection.
- Add optional `KAL` keep-alive support only after the basic connection workflow is reliable.

## Testing Strategy

- Unit-test command formatting and carriage-return termination.
- Unit-test response parsing for normal, malformed, empty, and unexpected responses.
- Unit-test XML parsing with captured `GSI`, `PSI`, and `GLT` payloads.
- Test timeout and disconnect behavior without hardware.
- Add protocol replay tests using captured serial traffic.
- Perform hardware acceptance testing with an SDS200 in Serial Port mode.
- Verify that the application does not send commands while disconnected.

## Acceptance Criteria

1. The application connects to a selected COM port.
2. The startup handshake confirms `MDL,SDS200`.
3. The firmware version from `VER` is displayed.
4. `GSI` is received and parsed successfully.
5. Live status updates are displayed through `PSI`.
6. Volume and squelch changes remain within SDS200 ranges.
7. Wrong-model connections are rejected with a clear message.
8. Timeouts, disconnects, malformed messages, and parser failures are actionable and logged.
9. Debug mode shows the complete `MDL` and `VER` handshake.
10. Each command and response can be correlated by timestamp and command name.
11. Raw payloads are available for diagnosing malformed responses.
12. Logging can be reduced or disabled without affecting scanner control.
13. Protocol unit tests pass without scanner hardware.

## Local Reference Material

Use `D:\OneDrive\Uniden Scanner` for supporting manuals, protocol notes, Sentinel materials, screen/control software sources, and related scanner research. Treat those files as secondary evidence unless a document is explicitly identified as an authoritative Uniden specification.

## Open Decisions Before Implementation

- Confirm the SDS200 serial-port baud, parity, stop bits, and handshake settings.
- Confirm whether the first UI should prioritize live control, status monitoring, or Favorites List browsing.
- Decide whether raw serial logs should be enabled by default in development builds only.
- Confirm the SDS200 firmware version available for hardware testing.
