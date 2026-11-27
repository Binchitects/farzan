# FIA_UAU.2 User authentication before any action

Common Criteria Part 2 security functional component, class FIA Identification and authentication, family FIA_UAU User authentication. In: CC:2022, CC 3.1 Revision 5.

## CC:2022

Hierarchical to: FIA_UAU.1
Dependencies: FIA_UID.1

### Elements

FIA_UAU.2.1 The TSF shall require each user to be successfully authenticated before allowing any other TSF-mediated actions on behalf of that user.

### Rationale

This component requires that a user is authenticated before any other TSF-mediated action can take place on behalf of that user.

### Levelling

FIA_UAU.2 requires that users are authenticated before any other action will be allowed by the TSF.

### Management

- Management of the authentication data by an administrator
- Management of the authentication data by the user associated with this data

### Audit

- minimal: Unsuccessful use of the authentication mechanism
- basic: All use of the authentication mechanism

## CC 3.1 Revision 5

Hierarchical to: FIA_UAU.1
Dependencies: FIA_UID.1

### Elements

FIA_UAU.2.1 The TSF shall require each user to be successfully authenticated before allowing any other TSF-mediated actions on behalf of that user.

### User notes

This component requires that a user is authenticated before any other TSF-mediated action can take place on behalf of that user.

### Levelling

FIA_UAU.2, requires that users are authenticated before any other action will be allowed by the TSF.

### Management

- management of the authentication data by an administrator;
- management of the authentication data by the user associated with this data.

### Audit

- minimal: Unsuccessful use of the authentication mechanism;
- basic: All use of the authentication mechanism.

## Protection Profiles that require FIA_UAU.2

2 PPs, PP-Modules and packages state it (2 current).

| PP | Scheme | How | As stated | Archived |
|---|---|---|---|---|
| BSI-CC-PP-9999-2026 Example Smart Card Protection Profile | DE | mandatory | FIA_UAU.2.1 |  |
| Protection Profile for Example Devices, Version 1.0 (PP_EX_V1.0) | US | mandatory | FIA_UAU.2.1 |  |

## Supporting Documents with its evaluation activities

- Protection Profile for Example Devices, Version 1.0: https://www.niap-ccevs.org/api/file/get_public_file/?file_id=901

## NIAP Technical Decisions

- TD9001 (Published): Clarification of FIA_UAU.2.1 for example devices

---

Source: the Common Criteria's XML (cc_2022.xml, cc3R5.xml), https://www.commoncriteriaportal.org/cc/

Terms: CCRA notice: re-use and publication allowed. The CCRA's: the governmental organizations that hold the copyright with ISO/IEC grant every CC user the re-use of the CC's and CEM's text, and the publication of documents that use it (the Legal Notice in each part, and https://www.commoncriteriaportal.org/cc/)
