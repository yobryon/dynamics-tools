# F&O Best Practice Rules Reference

Roster of every Best Practice rule shipped with the F&O server-side `xppbp.exe`,
extracted from the rule assemblies under `<PackagesLocalDirectory>/bin/`. Use
moniker names in `.dynamics-xpp/config.json -> bestPractices.suppress` to
silence rules per project, or in `xpp_bp_check(monikers=[...])` to drill into
specific findings at full detail.

**Total: 184 rules across 9 rule assemblies.**

| Category | Assembly | Rules |
| --- | --- | --- |
| UI Rules | `Microsoft.Dynamics.AX.Framework.BestPracticeFramework.UIRules.dll` | 2 |
| Code Style | `Microsoft.Dynamics.AX.Framework.CodeStyleRules.dll` | 22 |
| Data Access | `Microsoft.Dynamics.AX.Framework.DataAccessRules.dll` | 3 |
| Data Method | `Microsoft.Dynamics.AX.Framework.DataMethodRules.dll` | 6 |
| Deprecated Elements | `Microsoft.Dynamics.AX.Framework.DeprecatedElementsRules.dll` | 14 |
| Maintainability | `Microsoft.Dynamics.AX.Framework.MaintainabilityRules.dll` | 119 |
| OData Services | `Microsoft.Dynamics.AX.Framework.Services.OData.Rules.dll` | 12 |
| Static Code Validation | `Microsoft.Dynamics.AX.Framework.StaticCodeValidationRules.dll` | 5 |
| Test Code | `Microsoft.Dynamics.AX.Framework.TestCodeRules.dll` | 1 |

## UI Rules

Source: `Microsoft.Dynamics.AX.Framework.BestPracticeFramework.UIRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPErrorFormPartLocationPreviewPane` | BPErrorFormPartLocationPreviewPane: Form part '{0}' has PartLocation=PreviewPane | Checks that the Form.Parts collection does not have any PartLocation=PreviewPane. |
| `BPErrorFormStringTitleFieldWithDesignStyleAuto` | BPErrorFormStringTitleFieldWithDesignStyleAuto: String '{0}' has Style set to TitleField. TitleField should not be used on a form with Style=Auto. Use an appropriate form pattern. | Checks that a form with Auto style does not have any fields with the TitleField style. |

## Code Style

Source: `Microsoft.Dynamics.AX.Framework.CodeStyleRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPCheckParametersModified` | Parameter {0} in method {1} is modified inside the method. | This rule fails if a parameter of a method is modified inside a method. |
| `BPCheckSysObsoleteAttributeParametersMismatch` | All parameters for attribute SysObsolete need to be defined for the {0} {1}. | This rule fails if all the parameters for the attribute are not defined. |
| `BPEmptyCompoundStatement` | Empty Compound statement {...}. | Checks that compound statements are non-empty. |
| `BPErrorLabelIsText` | BPErrorLabelIsText: '{0}' is not a label ID. | Reports when string literal delimited with quotes contains a non-label. |
| `BPErrorMethodLabelInSingleQuotes` | Use double-quotation marks when referring to labels, instead of using single-quotation marks. | Checks that a label is delimited by double-quotation marks. |
| `BPErrorUnknownLabel` | Unknown label '{0}'. Labels are case-insensitive, but it is recommended to use upper casing when referring to legacy labels (such as the label id @SYS12345) and exact casing for modern labels (such as 'MyLabelId' in @MyLabelFile:MyLabelId). | Reports when a label does not exist. |
| `BPForbiddenMethod` | The method '{0}' can cause dangerous side-effects and should not be used. | Reports when the code calls a method which can have dangerous side effects, and the method should not be called. |
| `BPIndentationError` | The language element at ({0}, {1}) is not indented correctly. The element should start in column '{2}'. | Checks that X++ code has the correct column indentation for maintainability and consistency. |
| `BPLocalVariableNotUsed` | The local {0} '{1}' is not used. | Checks that all local variables and functions are actually used. |
| `BPNestedStatementsShouldBeInBraces` | When statements are nested, such as under if and while, enclose the body of nested statements in braces {...} even when the body contains only one statement. | Checks that nested statements are enclosed in braces. |
| `BPParameterNotUsed` | The parameter '{0}' is not used. | Checks that all parameters of a method are used. |
| `BPUnderscoresOnlyForParameters` | The use of underscores as a prefix is restricted to method and function parameters (rename '{0}' to '{1}'). | Checks that underscore ("_") is only used for parameters and not for any other constructs. |
| `BPXmlDocIllegalTag` | BPXmlDocIllegalTag: Tag '{0}' is not allowed here. | Checks the XML documentation for illegal tags. |
| `BPXmlDocMalformed` | BPXmlDocMalformed: The XML documentation for '{0}' is not well-formed. | Checks if the XML documentation is malformed. |
| `BPXmlDocNoDocumentationComments` | BPXmlDocNoDocumentationComments: No XML documentation headers are provided for '{0}'. | Checks if XML documentation is missing. |
| `BPXmlDocNoHelpfulInformation` | BPXmlDocNoHelpfulInformation: No helpful information is provided in the '{0}' tag for '{1}'. | Checks if XML documentation tags are empty. |
| `BPXmlDocNoReturns` | BPXmlDocNoReturns: The documentation header contains no 'returns' tag, yet the method '{0}' returns a non-void value. | Checks whether the return tag exists in XML documentation when method has a return type other than void. |
| `BPXmlDocNoSummaryTag` | BPXmlDocNoSummaryTag: No summary tag is provided. | Checks if the summary tag is provided in XML documentation. |
| `BPXmlDocParameterNotDescribed` | BPXmlDocParameterNotDescribed: Parameter '{0}' is not described in any '{1}' tag. | Checks whether parameter tags are provided for all the parameters. |
| `BPXmlDocParameterTagMustHaveOneAttributeCalledName` | BPXmlDocParameterTagMustHaveOneAttributeCalledName: The documentation header for a parameter must have exactly one attribute called 'name'. | Checks whether parameter tags have 'name' attribute. |
| `BPXmlDocReturnsVoid` | BPXmlDocReturnsVoid: The documentation header contains a 'returns' tag, yet the method '{0}' returns void. | Checks the return tag of XML documentation and matches it with actual return type of the method. |
| `BPXmlDocTagDoesNotDesignateExistingParameter` | BPXmlDocTagDoesNotDesignateExistingParameter: Name '{0}' in the '{1}' tag is not a parameter for this method. | Checks the parameter tags of XML documentation and matches them with method parameters. |

## Data Access

Source: `Microsoft.Dynamics.AX.Framework.DataAccessRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPErrorMethodDeleteFromNotUsed` | Consider modifying your code to delete a set of data records with one 'delete_from' statement. The 'while select ... .delete()' code design, which loops around a call to a singleton delete method, might be less efficient. | Detects a call to the 'delete' method within a 'while select' statement, where a 'delete_from' set-based statement might be more efficient. |
| `BPErrorMethodUnbalancedTtsbeginCommit` | {0} | Detects unpaired ttsBegin or ttsCommit statements. The transaction statements must be balanced within the same scope. |
| `BPErrorSelectUsingFirstOnly` | Consider using the 'firstonly' keyword for select from '{0}' since additional data rows are unused here. | Detects a 'select' statement that could be more efficient by adding the 'firstonly' keyword. |

## Data Method

Source: `Microsoft.Dynamics.AX.Framework.DataMethodRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPErrorFormDisplayMethodHasRequiredArgs` | Display method on form '{0}' cannot have required parameters. | Detects when a display method on a table has a required parameter. |
| `BPErrorNotAllowedDisplayMethod` | Display methods can be declared only on tables, forms, form datasources, reports, and report designs. | Detects display methods declared outside their permitted contexts: tables, forms, form datasources, reports, and report designs. |
| `BPErrorNotAllowedEditMethod` | Edit methods can be declared only on tables, forms, and form datasources. | Detects edit methods declared outside the legal context: tables, forms, and form datasources. |
| `BPErrorTableDisplayMethodHasRequiredArgs` | Display methods on table '{0}' cannot have required parameters. | Detects when a display method on a form has a required parameter. |
| `BPWarningDisplayMethodUpdateUnpredictable` | The display method '{0}' in '{1}' may not update as expected. | Checks that the values used for computing a display method's value are observable - form properties, data source fields, and fields with the FormObservable attribute are observable. |
| `BPWarningFieldMissingFormObservableAttribute` | The field '{0}' in '{1}' is used by the display method '{2}' in '{3}' and does not have the FormObservable attribute. | Checks that fields that contribute to the value of a display method have the FormObservable attribute. This allows the system to refresh a display method when a field that it depends on changes. |

## Deprecated Elements

Source: `Microsoft.Dynamics.AX.Framework.DeprecatedElementsRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPBreakpointStatementUsage` | BPBreakpointStatementUsage: The "breakpoint" statement has been deprecated and has no effect. | Reports the usage of "breakpoint" statements, which has been deprecated and has no effect. |
| `BPClientLocalFileAccess` | BPClientLocalFileAccess: Invalid call to {0}. Accessing the local file system on the client is not allowed. | Reports calls to APIs that access local files. Files cannot be created on the server tier. |
| `BPDeprecatedClass` | BPDeprecatedClass: {0} has been deprecated and should not be used. | Reports occurrences of deprecated classes and informs to not use it. |
| `BPDeprecatedClassMethod` | BPDeprecatedClassMethod: {0} has been deprecated and should not be used. | Reports the usage of deprecated class methods. |
| `BPDeprecatedControl` | BPDeprecatedControl: The '{0}' control has been deprecated and should not be used. | Reports the usage of deprecated controls. |
| `BPDeprecatedControlMethod` | BPDeprecatedControlMethod: The "{0}" control method has been deprecated and should not be used. | Reports the usage of deprecated control methods. |
| `BPDeprecatedDataSourceMethod` | BPDeprecatedDataSourceMethod: The "{0}" data source method has been deprecated and should not be used. | Reports the usage of deprecated data source methods. |
| `BPDeprecatedFormAndControlPropertySweeper` | BPDeprecatedFormAndControlPropertySweeper: The {0} property has been deprecated and should not be used. | Reports the usage of deprecated properties in forms and form controls. |
| `BPDeprecatedFormMethod` | BPDeprecatedFormMethod: The "{0}" form method has been deprecated and should not be used. | Reports the usage of deprecated form methods. |
| `BPDeprecatedUOMProductParameter` | BPDeprecatedUOMProductParameter: The product parameter in UnitOfMeasureConverter class method '{0}' is deprecated, use the EcoResProductUnitConverter class instead. | Reports the usage of deprecated product parameter in the UnitOfMeasureConverter class. |
| `BPDeprecatedUseObsoleteMethodsWhenWarehouseTransactionsEnabled` | {0} | Detects usage of the methods which will not be called the same way they were before the activation of the "Warehouse-specific inventory transactions" feature. |
| `BPDeprecatedUseObsoleteWHSContainerTransFields` | BPDeprecatedUseObsoleteWHSContainerTransFields: Detected a usage of the {0} field. This field is not populated for the warehouse containers using the warehouse-specific inventory transactions. | Detects usage of WHSContainerTrans.InventTransIdFrom and WHSContainerTrans.InventTransIdTo fields. |
| `BPDeprecatedUseObsoleteWHSWorkInventTransFields` | BPDeprecatedUseObsoleteWHSWorkInventTransFields: Detected a usage of the {0} field. This field is not populated for the warehouse works using the warehouse-specific inventory transactions. | Detects usage of WHSWorkInventTrans.InventTransIdFrom and WHSWorkInventTrans.InventTransIdTo fields. |
| `BPPrintStatementUsage` | BPPrintStatementUsage: Print statements are for debugging only and should not appear in production code. | Reports the usage of 'print' statements, which are for debugging and should not appear in production code. |

## Maintainability

Source: `Microsoft.Dynamics.AX.Framework.MaintainabilityRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPErrorAllFormDataSourceTablesAreExpectedToBeSaveDataPerCompany` | BPErrorAllFormDataSourceTablesAreExpectedToBeSaveDataPerCompany: The design property '{0}' is enabled, but the property '{1}' on all the data sources has an an incorrect value. Set the design property '{0}' to No to prevent the form from restarting in the previous company. | Reports that forms with the SetCompany property enabled do not contain any data sources with tables which have the SaveDataPerCompany property enabled. |
| `BPErrorAllFormDataSourceTablesAreNotExpectedToBeSaveDataPerCompany` | BPErrorAllFormDataSourceTablesAreNotExpectedToBeSaveDataPerCompany: The design property '{0}' is disabled, but the property '{1}' on all the data sources has an incorrect value. Set the design property '{0}' to Yes to ensure that the form restarts in the previous company. | Reports that forms with the SetCompany property disabled do not contain any data sources with tables which have the SaveDataPerCompany property disabled |
| `BPErrorCaptionIsCopyOfDataGroupLabel` | BPErrorCaptionIsCopyOfDataGroupLabel: The Caption property value of the group control is a copy of the Label property value for its associated table data group. | Reports when the Caption property value of the group control is a copy of the Label property value for its associated table data group. |
| `BPErrorCaptionNotDefined` | BPErrorCaptionNotDefined: No value is given for the Caption property. | Reports when no value is given for the Caption property. |
| `BPErrorClassNewNotProtected` | BPErrorClassNewNotProtected: The constructor (i.e. the 'new' method) should be protected. In addition, private constructors are allowed on final classes. | Checks that constructors are protected. |
| `BPErrorControlHelpIsCopyOfEnumHelp` | BPErrorControlHelpIsCopyOfEnumHelp: The control's help text is the same as the help information for the base enum. | Reports when the control's help text is the same as the help text for its base enum. |
| `BPErrorControlHelpIsCopyOfExtendedHelp` | BPErrorControlHelpIsCopyOfExtendedHelp: The control help text is a copy of the help for its extended data type. | Reports when the control's help text is the same as the help text for its extended data type. |
| `BPErrorControlLabelIsCopyOfEnumLabel` | BPErrorControlLabelIsCopyOfEnumLabel: The label of the control is a copy of the label for its base enum, which is not allowed. | Reports when the label of the control is a copy of the label for its base enun, because such a copy is not allowed. |
| `BPErrorControlLabelIsCopyOfExtendedLabel` | BPErrorControlLabelIsCopyOfExtendedLabel: The label of the control is a copy of the label for its extended data type, which is not allowed. | Reports when the label of the control is a copy of the label for its extended data type, because such a copy is not allowed. |
| `BPErrorControlsNotInSpecificDesign` | BPErrorControlsNotInSpecificDesign: The control is not specified in a design. | Checks that all controls are included in a design. |
| `BPErrorCustomFilterGroupHasToggleCheckbox` | BPErrorCustomFilterGroupHasToggleCheckbox: A custom filter group should not have a toggle checkbox. | Reports that a group with the CustomFilter style or with the CustomFilters or CustomAndQuickfilters pattern has a checkbox with the Toggle style. |
| `BPErrorCustomFilterGroupInputControlUnbound` | The control {0} inside this custom filter group (group with Style=CustomFilter) must not have any value specified for its DataSource, DataField, ReferenceField, and DataGroup properties. Set the values to empty. | Detects that a control for a custom filter group is unbound. |
| `BPErrorDataSourceOnWorkspaceStyleForm` | BPErrorDataSourceOnWorkspaceStyleForm: Form '{0}' with Style=Workspace should not have data sources. | Reports that forms with Style=Workspace should not contain data sources. |
| `BPErrorDeveloperDocumentationNotDefined` | BPErrorDeveloperDocumentationNotDefined: The mandatory property DeveloperDocumentation is not specified. | Checks if the mandatory property DeveloperDocumentation is specified. |
| `BPErrorDisplayEditNoExtendedReturnType` | BPErrorDisplayEditNoExtendedReturnType: Display and Edit methods must return values of an extended data type. | Checks if the return types of display and edit methods are extended data types. |
| `BPErrorDropDialogButtonInvalidTarget` | BPErrorDropDialogButtonInvalidTarget: Drop dialog button '{0}' references a form whose style is not DropDialog. Please ensure that the target form uses style DropDialog. | Reports when a DropDialogButton in a form references a form whose style is not DropDialog. |
| `BPErrorDutyHasNoPrivileges` | BPErrorDutyHasNoPrivileges: The duty '{0}' has no privilege references. | All duties should have at least one privilege. |
| `BPErrorDutyNotCoveredByRole` | BPErrorDutyNotCoveredByRole: The duty '{0}' is not referenced on any role. | All duties should be part of a role. |
| `BPErrorEDTNotMigrated` | BPErrorEDTNotMigrated: The relation under the extended data type (EDT) '{0}' must be migrated to table relation. Consider using EDT relation migration tool. | Checks if all EDT relations have been migrated to table relations. |
| `BPErrorEdtFormHelpIsLookup` | BPErrorEdtFormHelpIsLookup: The FormHelp form '{0}' for this extended data type has the wrong style. Choose a form that has either its Style property set to Lookup or its Pattern set to CustomLookup. | Reports that a FormHelp form for an extended data type is not a lookup form. |
| `BPErrorEdtHelpIsCopyOfEnumHelp` | BPErrorEdtHelpIsCopyOfEnumHelp: The HelpText property value of the extended data type is the same as the Help property value on its base enum. Make each value specific to its intended use. | Reports when the HelpText property value of the extended data type is the same as the Help property value on its base enum. |
| `BPErrorEdtHelpIsCopyOfExtendedHelp` | BPErrorEdtHelpIsCopyOfExtendedHelp: The HelpText property value of the extended data type is the same as the HelpText property value on extended data type it is extending from. | Reports when the HelpText property value of the extended data type is the same as the HelpText property value on extended data type it is extending from. |
| `BPErrorEdtLabelIsCopyOfEnumLabel` | BPErrorEdtLabelIsCopyOfEnumLabel: The Label property value of the extended data type is the same as the Label property value on its base enum. | Reports when the Label property value of the extended data type is the same as the Label property value on its base enum. Make each value specific to its intended use. |
| `BPErrorEdtLabelIsCopyOfExtendedLabel` | BPErrorEdtLabelIsCopyOfExtendedLabel: The Label property value of the extended data type is the same as the Label property value on the extended data type it is extending from. | Reports when the Label property value of the extended data type is the same as the Label property value on the extended data type it is extending from. |
| `BPErrorEdtSameLabelAndHelp` | BPErrorEdtSameLabelAndHelp: The extended data type has the same string value for its Label and HelpText properties. Give each property a value that is specific to its intended use. | Reports when an extended data type has the same string value for its Label and HelpText properties. |
| `BPErrorEdtStringNotLeftJustified` | BPErrorEdtStringNotLeftJustified: A string extended data type has its Adjustment property set to 'Right', when it should be set to 'Left'. | Reports when a string extended data type has its Adjustment property set to 'Right', when it should be set to 'Left'. |
| `BPErrorEmptyTableMethod` | {0} table has an empty {1} method. | This rule checks for table methods with no source code. |
| `BPErrorEnumSameLabelAndHelp` | BPErrorEnumSameLabelAndHelp: The base enum has the same string value for its Label and Help properties. Give each property a value that is specific to its intended use. | Reports when a base enum has the same string value for its Label and Help properties. |
| `BPErrorFieldHelpIsCopyOfEnumHelp` | BPErrorFieldHelpIsCopyOfEnumHelp: The Help property value of the field is the same as the Help property value of its base enum. | Reports when the Help property value of the field is the same as the Help property value of its base enum. |
| `BPErrorFieldHelpIsCopyOfExtendedHelp` | BPErrorFieldHelpIsCopyOfExtendedHelp: The Help property value of the field is the same as the HelpText property value of its extended data type. | Reports when the Help property value of the field is the same as the HelpText property value of its extended data type. |
| `BPErrorFieldLabelIsCopyOfEnumHelp` | BPErrorFieldLabelIsCopyOfEnumHelp: The Label property value of the field is a copy of the Help property value for its base enum. | Reports when the Label property value of the field is a copy of the Help property value for its base enum. |
| `BPErrorFieldLabelIsCopyOfEnumLabel` | BPErrorFieldLabelIsCopyOfEnumLabel: The Label property value of the field is a copy of Label property value for its base enum. | Reports when the Label property value of the field is a copy of the Label property value for its base enum. |
| `BPErrorFieldLabelIsCopyOfExtendedHelp` | BPErrorFieldLabelIsCopyOfExtendedHelp: The Label property value of the field is a copy of the HelpText property value of its extended data type. | Reports when the Label property value of the field is a copy of the HelpText property value of its extended data type. |
| `BPErrorFieldLabelIsCopyOfExtendedLabel` | BPErrorFieldLabelIsCopyOfExtendedLabel: The Label property value of the field is a copy of the Label property value for its extended data type. | Reports when the Label property value of the field is a copy of the Label property value for its extended data type. |
| `BPErrorFilterPropertiesControlsCannotHaveOverrides` | BPErrorFilterPropertiesControlsCannotHaveOverrides: Form control '{0}' on form '{1}' has one or more of these properties defined: 'FilterDataSource', 'FilterControl' and 'FilterExpression'. A control with these properties must not have method overrides. | Reports when a form control has filter properties and it has modified or selection changed overrides. |
| `BPErrorFilterPropertiesOnlyOnTemplatedForms` | BPErrorFilterPropertiesOnlyOnTemplatedForms: Form control '{0}' on form '{1}' has one or more of these properties defined: 'FilterDataSource', 'FilterControl' and 'FilterExpression'. These properties can only be applied in forms with 'ListPage' or 'DetailsPage' template. | Reports when a form control has filter properties and its host form is not a templated form. |
| `BPErrorFormCaptionIsEmpty` | BPErrorFormCaptionIsEmpty: The caption of a form should not be empty. | Reports that a form has an empty caption. |
| `BPErrorFormDataSourceJoinLimit` | BPErrorFormDataSourceJoinLimit: Too many joins ('{0}') in a single query datasource tree. The recommended limit is '{1}'. Consider reducing the number of joins in this form. | Checks whether the join trees exceed the recommended limit. |
| `BPErrorFormDataSourceMustNotBeUsingEntities` | The form data source '{0}' can't be bound to a data entity. | Reports when the form data source is bound to a data entity. |
| `BPErrorFormDataSourceTableUnknown` | BPErrorFormDataSourceTableUnknown: Table on data source '{0}' is unknown. | Reports that forms do not contain data sources without a valid table reference. |
| `BPErrorFormGridValidTimeStateColumnsNotFound` | BPErrorFormGridValidTimeStateColumnsNotFound: Form '{0}' contains a data source which has its ValidTimeStateAutoQuery property set to DateRange, therefore the form must also have a grid with the date fields ValidFrom and ValidTo. | Reports when a form contains a data source which has its ValidTimeStateAutoQuery property set to DateRange, yet the form lacks the thereby required grid with the date fields ValidFrom and ValidTo. |
| `BPErrorFormPartControlTargetFormWithoutFormPartStyle` | BPErrorFormPartControlTargetFormWithoutFormPartStyle: The forms used in form parts need to have have style of FormPart. | Reports that a form has form parts that do not have style of FormPart. |
| `BPErrorFormRealControlNumberOfDecimals` | BPErrorFormRealControlNumberOfDecimals: The MinNoOfDecimals property value is equal to or greater than the value of the NoOfDecimals property in a form control of the real data type. | Reports when the MinNoOfDecimals property value is equal to or greater than the value of the NoOfDecimals property in a form control of the real data type. |
| `BPErrorFormSplitterGroupNotEmpty` | BPErrorFormSplitterGroupNotEmpty: Group control modeled as a splitter is left empty. | Checks that a group control modeled as a splitter is left empty. |
| `BPErrorFormWorkflowEnabledWithNoActionPane` | BPErrorFormWorkflowEnabledWithNoActionPane: WorkFlow enabled form has an action pane control. | Checks that a workflow enabled form has an action pane control. |
| `BPErrorHelpDefined` | BPErrorHelpDefined: Help is defined on a control that cannot display help. | Reports when help is defined on a control that cannot display help. |
| `BPErrorHelpIsText` | BPErrorHelpIsText: Property '{0}' must have a label ID as its value, and '{1}' is not a label ID. | Reports when property must have a label ID as its value, but does not have a label ID as its present value. |
| `BPErrorHorizontalFieldsButtonGroupButtonLimit` | BPErrorHorizontalFieldsButtonGroupButtonLimit: Since it is using the HorizontalFieldsAndButtonGroup pattern, the group control '{0}' should not have more than three buttons. | Detects that a group using the HorizontalFieldsAndButtonGroup pattern contains more than three buttons. |
| `BPErrorLabelAndHelpAreEqual` | BPErrorLabelAndHelpAreEqual: The string values for the Label and Help or HelpText properties are the same. Make each value specific to its intended use. | Reports when the string values for the Label and Help or HelpText properties are the same. |
| `BPErrorLabelIsText` | BPErrorLabelIsText: Property '{0}' must have a label ID as its value, and '{1}' is not a label ID. | Reports when property must have a label ID as its value, but does not have a label ID as its present value. |
| `BPErrorLabelNotDefined` | BPErrorLabelNotDefined: No label is given for the '{0}' property. | Reports when a property is missing a label. |
| `BPErrorLabelWrongEndSign` | BPErrorLabelWrongEndSign: A label may not end with a period ('.'). | Reports when a label ends with a period ('.'). |
| `BPErrorMenuItemNotCoveredByPrivilege` | BPErrorMenuItemNotCoveredByPrivilege: '{0}' '{1}' is not covered by privilege. | All Menu Item should be covered by at least one privilege |
| `BPErrorNavigationBarMenuExtensionInvalidType` | BPErrorNavigationBarMenuExtensionInvalidType: '{0}' contains elements other than MenuItems adjust your menu structure. | Checks that Navigtaion Bar menus don't have invalid types |
| `BPErrorNavigationBarMenuInvalidType` | BPErrorNavigationBarMenuInvalidType: '{0}' contains elements other than MenuItems adjust your menu structure. | Checks that Navigtaion Bar menus don't have invalid types |
| `BPErrorNeitherControlInSpecificDesign` | BPErrorNeitherControlInSpecificDesign: No control is specified in design. | Checks if any control is specified in the design. |
| `BPErrorNormalImagePropertyInvalid` | BPErrorNormalImagePropertyInvalid: The NormalImage property on the control does not correspond to a valid symbol name. | Reports that the NormalImage property on a control does not correspond to a valid symbol name. |
| `BPErrorPreventDenyGrant` | Resource '{0}' grant is set to 'NoAccess'. | An explicit global Deny trumps all other explicit and inferred access to the resource. This could conflict with other user role assignments. |
| `BPErrorPreventRedundantChildNodeGrant` | Resource '{0}' is granted with same access as parent '{1}' and is redundant. | Resource grant is redundant when it maches the grant to parent resource. Resources inherit access from parent implicitely when not specified. |
| `BPErrorPreventRedundantEntryPointFormControlOverrideGrant` | Form control '{0}' has NeededPermission set to '{1}' which is already granted by entry point context '{2}' and makes this redundant. | At runtime form control access is inferred based on entry point context. For instance if the entry point grants 'Update' and the control's NeededPermission is set to 'Read' there is no need to define an entry point form control override. |
| `BPErrorPreventRedundantEntryPointUnsetGrant` | Entry point '{0}' grant is set to 'Unset' | Entry point grant that has not been set is redundant. |
| `BPErrorPreventRedundantUnsetGrant` | Resource '{0}' grant is set to 'Unset'. | Grants set to 'Unset' are redundant when they have no child nodes. |
| `BPErrorPrivilegeIsEmpty` | BPErrorPrivilegeIsEmpty: The privilege '{0}' is empty. | All privileges should grant at least one resouce. |
| `BPErrorPrivilegeNotCoveredByDuty` | BPErrorPrivilegeNotCoveredByDuty: The privilege '{0}' is not referenced on any duty. | All privileges should be part of a duty. |
| `BPErrorSegmentedEntryControlCheckAccountTypeNotDefined` | BPErrorSegmentedEntryControlCheckAccountTypeNotDefined: The Segmented Entry control is using the DimensionDynamicAccountController Controller class so it must also specify the Account Type field. | Checks if a Segmented Entry control uses the DimensionDynamicAccountController Controller class and if the Account Type field has a value. |
| `BPErrorSegmentedEntryControlCheckUseCustomLookupMethodNotDefined` | BPErrorSegmentedEntryControlCheckUseCustomLookupMethodNotDefined: The Segmented Entry control has a 'lookup' method so it must also override the 'checkUseCustomLookup' method. | Checks if a Segmented Entry control overrides the 'lookup' method but does not override the 'checkUseCustomLookup' method. |
| `BPErrorSegmentedEntryControlControllerNotDefined` | BPErrorSegmentedEntryControlControllerNotDefined: The Segmented Entry Control's Controller property is not set. | Reports when the Controller property is not set on a Segmented Entry Control. |
| `BPErrorSubMenuMenuItemNotAllowed` | BPErrorSubMenuMenuItemNotAllowed: The submenu '{0}' references menu item '{1}'. Menu items are not allowed on submenus. | Reports when a submenu references a menu item; menu items are not allowed on submenus. |
| `BPErrorSuppressionListMissingJustification` | BPErrorSuppressionListMissingJustification: The rule '{0}' with path '{1}' cannot be suppresed without a justification. | Reports that every suppressed best practice rule has a justification. |
| `BPErrorTableDelConfigKeyConflict` | BPErrorTableDelConfigKeyConflict: Table '{0}' with the 'DEL_' prefix has configuration '{1}' instead of SysDeletedObjects. | Checks that tables with the 'DEL_' prefix have the 'SysDeletedObjects' configuration key. |
| `BPErrorTableDelPrefixConflict` | BPErrorTableDelPrefixConflict: The table with SysDeletedObjects configuration key has no 'DEL_' prefix. | Checks if a table with SysDeletedObjects configuration key uses the 'DEL_' prefix. |
| `BPErrorTableDeleteActionBothDirections` | BPErrorTableDeleteActionBothDirections: The table does not have delete actions in both directions. | Checks if tables have delete actions in both directions. |
| `BPErrorTableDeleteActionUnknownTable` | BPErrorTableDeleteActionUnknownTable: The delete action is related to an unknown table. | Checks if the delete action is related to an unknown table. |
| `BPErrorTableDuplicateUITextField` | BPErrorTableDuplicateUITextField: Fields must have unique labels. '{0}' has the same label. | Reports when a string is used as a label for more than one field in a table. Each field label must be unique. |
| `BPErrorTableEditMethodWrongNumberArgs` | BPErrorTableEditMethodWrongNumberArgs: The table edit method must have a boolean 'set' parameter, and a parameter that matches the type of the control which uses the method. | Checks if the table edit method has a boolean 'set' parameter, and a parameter that matches the type of the control which uses the method. |
| `BPErrorTableFieldConfigurationKeyIsCopyOfEDT` | BPErrorTableFieldConfigurationKeyIsCopyOfEDT: The table field's configuration key is the same as the extended datatype's configuration key. | Checks whether a table field's configuration key is the same as the extended data type configuration key. |
| `BPErrorTableFieldConfigurationKeyIsCopyOfEnumeration` | BPErrorTableFieldConfigurationKeyIsCopyOfEnumeration: Enumeration field configuration key should not be same as configuration key of enumeration. | Checks whether a table field's configuration key is a copy of the enumeration type's configuration key. |
| `BPErrorTableFieldDelConfigKeyConflict` | BPErrorTableFieldDelConfigKeyConflict: Field '{0}' with the 'DEL_' prefix has configuration '{1}' instead of 'SysDeletedObjects'. | Checks that fields in a table with the 'DEL_' prefix have the 'SysDeletedObjects' configuration key. |
| `BPErrorTableFieldHasSameNameAsMethod` | BPErrorTableFieldHasSameNameAsMethod: Fields cannot have the same name as display or edit methods. | Checks whether fields have the same name as display or edit methods. |
| `BPErrorTableFieldLabelAndHelpAreEqual` | BPErrorTableFieldLabelAndHelpAreEqual: A table's field has the same value for its Label and HelpText properties. | Reports when a table's field has the same value for its Label and HelpText properties. |
| `BPErrorTableFieldNotDefinedUsingType` | BPErrorTableFieldNotDefinedUsingType: Field must be defined using a type. | Checks that table fields are defined using a type. |
| `BPErrorTableFieldNotInFieldGroup` | BPErrorTableFieldNotInFieldGroup: The table's field is not a member of a field group. | Reports when the table's field is not a member of a field group. |
| `BPErrorTableFieldUsesTableId` | BPErrorTableFieldUsesTableId: Table fields that refer to table IDs must use RefTableId or an extended data type derived from it. | Checks if a table field directly uses TableId extended data type. The field should instead use RefTableId or a derived extended data type. |
| `BPErrorTableIndexDelConfigKeyConflict` | BPErrorTableIndexDelConfigKeyConflict: An index with the 'DEL_' prefix has configuration instead of SysDelete. | Checks if an index with the 'DEL_' prefix has configuration instead of SysDelete. |
| `BPErrorTableIndexFieldDeprecated` | BPErrorTableIndexFieldDeprecated: A non-deprecated index has a deprecated field. | Checks if non-deprecated indexes have deprecated fields. |
| `BPErrorTableIndexWithoutFields` | BPErrorTableIndexWithoutFields: The index has no fields. | Checks that indexes have fields. |
| `BPErrorTableMissingFormRef` | BPErrorTableMissingFormRef: No form ref is specified. | Checks if form ref is specified. |
| `BPErrorTableMissingGroupAutoReport` | BPErrorTableMissingGroupAutoReport: The table has no fields in the AutoReport field group. | Checks that a table has fields in the AutoReport field group. |
| `BPErrorTableMultipleCompositionRelations` | BPErrorTableMultipleCompositionRelations: There are more than one composition relations in this table. | Checks if there are more than one composition relations in this table. |
| `BPErrorTableNaturalKeyWithRecordID` | BPErrorTableNaturalKeyWithRecordID: The RecordID field is part of the NaturalKey index. | Checks if RecordID field is part of the NaturalKey index. |
| `BPErrorTableNoCaching` | BPErrorTableNoCaching: No caching is set up for the table. | Checks if caching is set up for the Table. |
| `BPErrorTableNoClusteredIndex` | BPErrorTableNoClusteredIndex: This table has no clustered index. | Checks if the table has a clustered index. |
| `BPErrorTableNoPrimaryIndex` | BPErrorTableNoPrimaryIndex: The table has no primary index. | Checks if the table has a primary index. |
| `BPErrorTableOneIndexNotCluster` | BPErrorTableOneIndexNotCluster: The table has only one index and that index is not defined as a clustered index. | Checks that if only one index is provided for a table, that index is a clustered index. |
| `BPErrorTableOverlappingIndex` | BPErrorTableOverlappingIndex: The index is overlapped by another index. | Checks if an index is overlapped by another index. |
| `BPErrorTablePrimaryIndexNotUnique` | BPErrorTablePrimaryIndexNotUnique: The primary index is not unique. | Checks that a primary index is unique. |
| `BPErrorTablePrimaryKeyEditable` | BPErrorTablePrimaryKeyEditable: The primary key must not be editable. | Checks if the primary key is editable. |
| `BPErrorTablePrimaryKeyNotMandatory` | BPErrorTablePrimaryKeyNotMandatory: The primary key must be mandatory. | Checks if the primary key is mandatory. |
| `BPErrorTableRelationNoFields` | BPErrorTableRelationNoFields: Relation '{0}' has no fields. | Checks if the table relation has relationship constraints. |
| `BPErrorTableRelationshipForeignKeyToShort` | BPErrorTableRelationshipForeignKeyToShort: If Adjustment is set to 'Left', the field's StringSize for must be greater than or equal to the size of its related field of table. | Checks that the table field's 'StringSize' is greater than or equal to the size of its related field of table if the adjustment property is set to 'Left'. |
| `BPErrorTableRelationshipPropertiesCompleteness` | BPErrorTableRelationshipPropertiesCompleteness: Relation properties [{0}] for table relation '{1}' are not set. | Checks table relationship for completeness by verifying if all required properties or the relationship are specified. |
| `BPErrorTableRelationshipPropertiesCorrectness` | The relation property named '{0}' has a value of '{1}', but the value should be '{2}'. | Checks the correctness of table relationship properties. |
| `BPErrorTableReplacementKeyNotSpecified` | BPErrorTableReplacementKeyNotSpecified: Tables having multiple alternate keys must specify a replacement key. | Checks if tables having multiple alternate keys specify replacement keys. |
| `BPErrorTableSysDeleteFieldIndex` | BPErrorTableSysDeleteFieldIndex: The unique index contains field(s) with the 'SysDelete' configuration key. Remove these fields from the index. | Checks if a unique index contains fields with the 'SysDelete' configuration key. |
| `BPErrorTableTPFIntegrity` | BPErrorTableTPFIntegrity: The AOSAuthorization property is not applied consistently in the chain of inheritance. Ensure that all tables have either authorization enabled or disabled. | Checks the usage of AOSAuthorization property for a table in chain of inheritance. |
| `BPErrorTableTitleField1NotDeclared` | BPErrorTableTitleField1NotDeclared: No field has been specified for the Title Field1 property. | Checks if a field has been specified for the Title Field1 property. |
| `BPErrorTableTitleField2NotDeclared` | BPErrorTableTitleField2NotDeclared: No field has been specified for the 'Title Field2' property. | Checks if a field has been specified for the 'Title Field2' property. |
| `BPErrorTableTitleFieldDeprecated` | BPErrorTableTitleFieldDeprecated: A deprecated field is used as a title field. | Checks if a a deprecated field is used as title field. |
| `BPErrorTableUnknownFormRef` | BPErrorTableUnknownFormRef: The form ref specified is not known. | Checks if an unknown form ref is specified. |
| `BPErrorTypeExtendsRecId` | BPErrorTypeExtendsRecId: The extended data type (EDT) inherits directly from the RecId EDT. Instead, it must inherit from the RefRecId EDT. | Reports when an application extended data type (EDT) inherits directly from the RecId EDT. |
| `BPErrorTypeExtendsTableId` | BPErrorTypeExtendsTableId: The extended data type (EDT) inherits directly from the TableId EDT. Instead, it must inherit from the RefTableId EDT. | Reports when an application extended data type (EDT) inherits directly from the TableId EDT. |
| `BPErrorTypeFieldsIncompatible` | BPErrorTypeFieldsIncompatible: The fields in the relation '{0}' are incompatible. '{1}.{2}' is '{3}' characters too short. | Checks the compatibility of fields in table relationships. |
| `BPErrorUnknownLabel` | Unknown label '{0}'. Labels are case-insensitive, but it is recommended to use upper casing when referring to legacy labels (such as the label id @SYS12345) and exact casing for modern labels (such as 'MyLabelId' in @MyLabelFile:MyLabelId). | Reports when a label does not exist. |
| `BPErrorWebsiteHostControlUrlInvalid` | BPErrorWebsiteHostControlUrlInvalid: Control '{0}' has an invalid or insecure URL value | Checks that the URL value of Website Host controls are empty or are valid HTTPS URLs |
| `BPReportLabelNotFound` | The label '{0}' cannot be loaded. Verify the label is defined and that the value for the label has been set. | Checks that labels used in reports can loaded. |
| `BPTablePrimaryKeyIsNotAlternateKey` | The index '{1}' specified as Primary key on table '{2}' is not described as an Alternate key. | Checks that the index referenced as Primary key on a table, is also described as an Alternate key. |
| `BPTableWithRecIdIndexMissingReplacementKey` | The table '{1}' has the RecId index enabled, but the Replacement Key is not specified even though at least one Alternate key exists. | Checks that the Replacement key is specified for tables with RecId index enabled and at least one Alternate key exists. |
| `BPWarningFormDesignOrControlsWithCustomPattern` | BPWarningFormDesignOrControlsWithCustomPattern: '{0}' is using the 'Custom' pattern. Please apply one of the other available patterns. | Checks whether the custom pattern is applied to the form design and controls. |
| `BPWarningMainMenuDepthExceeded` | BPWarningMainMenuDepthExceeded: '{0}' is at a depth of '{1}', but the maximum rendered menu depth is '{2}'. Adjust your menu structure such that its depth does not exceed this limit. | Checks that main menus don't exceed maximum preferred depth. |

## OData Services

Source: `Microsoft.Dynamics.AX.Framework.Services.OData.Rules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPODataActionCollectionNotSpecified` | BPODataActionCollectionNotSpecified: A matching SysODataCollectionAttribute could not be found. | Checks that SysODataCollectionAttribute is specified for OData Actions containing collection parameters or return types. |
| `BPODataActionNameAlreadyUsed` | BPODataActionNameAlreadyUsed: The Action name '{0}' is already specified in another SysODataActionAttribute. | Checks that OData Action names are unique. |
| `BPODataActionNameInvalid` | BPODataActionNameInvalid: The 'name' parameter value '{0}' is not a valid identifier. Update to a valid OData identifier. | Checks that OData Action names are valid OData identifiers. |
| `BPODataActionNameNotSpecified` | BPODataActionNameNotSpecified: The 'name' parameter is not specified. | Checks that OData Action names are specified. |
| `BPODataActionParameterTypeInvalid` | BPODataActionParameterTypeInvalid: The parameter type for parameter '{0}' is not a valid OData parameter type. | Checks that OData Action parameters are valid OData types. |
| `BPODataActionReturnTypeInvalid` | BPODataActionReturnTypeInvalid: The return type is not a valid OData return type. | Checks that OData Action return types are valid OData types. |
| `BPODataFieldNameConflicts` | BPODataFieldNameConflicts: The field has the same name as another view field. | Checks that OData Properties do not conflict due to view fields. |
| `BPODataRelationLinesEmpty` | BPODataRelationLinesEmpty: The relation has no lines. | Checks that view relationship lines are specified on OData Relationships. |
| `BPODataRelationPropertyInvalid` | BPODataRelationPropertyInvalid: The value '{0}' in the relation property '{0}' is not a valid identifier. Change the name to a valid OData identifier. | Checks that generated OData Property names from view relationships are valid OData identifiers. |
| `BPODataRelationPropertyNotSpecified` | BPODataRelationPropertyNotSpecified: The relation property named '{0}' is not specified. | Checks that view relationship metadata is specified for OData Resources. |
| `BPODataRelationRelatedRoleNameAlreadyUsed` | BPODataRelationRelatedRoleNameAlreadyUsed: The RelatedTableRole property on relation '{0}' defined on view '{1}' specifies the same name as a field, display or edit method, or relationship role already defined on the view. | Checks that view relationship metadata does not generate conflict OData Properties. |
| `BPODataRelationRoleNameAlreadyUsed` | BPODataRelationRoleNameAlreadyUsed: The Role property on relation '{0}' defined on view '{1}' specifies the same name as a field, display or edit method, or relationship role already defined on view '{2}.' | Checks that view relationship metadata does not generate conflict OData Properties. |

## Static Code Validation

Source: `Microsoft.Dynamics.AX.Framework.StaticCodeValidationRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPFormatSpecifierInvalid` | The number of parameters ({0}) doesn't match with the number of format specifiers: ({1}). | Verifies the number of format specifiers equals the number of parameters |
| `BPFormatSpecifierIsZero` | The format specifier %0 cannot be used. | Verifies the format specifier %0 is not used. |
| `BPNonContiguousFormatSpecifiers` | Not all specifier numbers are used in the format string. The following is/are not used : {0}. | Verifies that there are no gaps in the argument specifiers in format strings. |
| `BPUnnecessaryStrFmtCall` | The call to strfmt is unnecessary since the format string does not contain any placeholders and no arguments are provided. | Raises a warning when unnecessary calls to the strFmt function are made, such as strFmt('hello') |
| `BPUnusedStrFmtArgument` | The placeholder '%{0}' to strFmt is not used in the format string. | Verifies the highest format specifier isn't lower than the given parameters. |

## Test Code

Source: `Microsoft.Dynamics.AX.Framework.TestCodeRules.dll`

| Moniker | Message | Description |
| --- | --- | --- |
| `BPTestTransactionModelAutoRollbackNotUsed` | Use the enumeration value '{0}' instead of '{1}', on the attribute '{2}' that decorates a class. | Detects when the SysTestTransaction attribute on a class should switch to using the enum value TestTransactionMode::AutoRollback, which provide better performance on test execution. |
