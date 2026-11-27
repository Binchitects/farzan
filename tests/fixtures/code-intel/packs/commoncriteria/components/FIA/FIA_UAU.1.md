# FIA_UAU.1 Timing of authentication

Common Criteria Part 2 security functional component, class FIA Identification and authentication, family FIA_UAU User authentication. In: CC:2022, CC 3.1 Revision 5.

## CC:2022

Hierarchical to: no other components.
Dependencies: FIA_UID.1

### Elements

FIA_UAU.1.1 The TSF shall allow [assignment: list of TSF mediated actions] on behalf of the user to be performed before the user is authenticated.
FIA_UAU.1.2 The TSF shall require each user to be successfully authenticated before allowing any other TSF-mediated actions on behalf of that user.

### Operations

- Assignment (list of TSF mediated actions): the author of a PP, PP-Module, functional package or ST specifies a list of TSF-mediated actions that can be performed by the TSF on behalf of a user before the claimed identity of the user is authenticated. This list cannot be empty. If no actions are appropriate, component FIA_UAU.2 should be used instead.

### Rationale

This component requires that the author of a PP, PP-Module, functional package or ST define the TSF-mediated actions that can be performed by the TSF on behalf of the user before the claimed identity of the user is authenticated. The TSF-mediated actions should have no security concerns with users incorrectly identifying themselves prior to being authenticated. For all other TSF-mediated actions not in the list, the user shall be authenticated before the action can be performed by the TSF on behalf of the user.

This component cannot control whether the actions can also be performed before the identification took place. This requires the use of either FIA_UID.1 or FIA_UID.2 with the appropriate assignments.

### Levelling

FIA_UAU.1, allows a user to perform certain actions prior to the authentication of the user's identity.

### Management

- Management of the authentication data by an administrator
- Management of the authentication data by the associated user
- Managing the list of actions that can be taken before the user is authenticated

### Audit

- minimal: Unsuccessful use of the authentication mechanism
- basic: All use of the authentication mechanism
- detailed: All TSF mediated actions performed before authentication of the user

## CC 3.1 Revision 5

Hierarchical to: no other components.
Dependencies: FIA_UID.1

### Elements

FIA_UAU.1.1 The TSF shall allow [assignment: list of TSF mediated actions] on behalf of the user to be performed before the user is authenticated.
FIA_UAU.1.2 The TSF shall require each user to be successfully authenticated before allowing any other TSF-mediated actions on behalf of that user.

### Operations

- Assignment (list of TSF mediated actions): the PP/ST author should specify a list of TSF-mediated actions that can be performed by the TSF on behalf of a user before the claimed identity of the user is authenticated. This list cannot be empty. If no actions are appropriate, component FIA_UAU.2 should be used instead. An example of such an action might include the request for help on the login procedure.

### User notes

This component requires that the PP/ST author define the TSF-mediated actions that can be performed by the TSF on behalf of the user before the claimed identity of the user is authenticated. The TSF-mediated actions should have no security concerns with users incorrectly identifying themselves prior to being authenticated. For all other TSF-mediated actions not in the list, the user must be authenticated before the action can be performed by the TSF on behalf of the user.

This component cannot control whether the actions can also be performed before the identification took place. This requires the use of either FIA_UID.1 or FIA_UID.2 with the appropriate assignments.

### Levelling

FIA_UAU.1, allows a user to perform certain actions prior to the authentication of the user's identity.

### Management

- management of the authentication data by an administrator;
- management of the authentication data by the associated user;
- managing the list of actions that can be taken before the user is authenticated.

### Audit

- minimal: Unsuccessful use of the authentication mechanism;
- basic: All use of the authentication mechanism;
- detailed: All TSF mediated actions performed before authentication of the user.

## Protection Profiles that require FIA_UAU.1

None of the PPs in this pack states it.

---

Source: the Common Criteria's XML (cc_2022.xml, cc3R5.xml), https://www.commoncriteriaportal.org/cc/

Terms: CCRA notice: re-use and publication allowed. The CCRA's: the governmental organizations that hold the copyright with ISO/IEC grant every CC user the re-use of the CC's and CEM's text, and the publication of documents that use it (the Legal Notice in each part, and https://www.commoncriteriaportal.org/cc/)
