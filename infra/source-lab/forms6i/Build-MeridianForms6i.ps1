param(
    [ValidateSet('Build', 'Validate')][string]$Action = 'Build',
    [string]$OracleHome = 'C:\orant',
    [string]$OutputDirectory = 'C:\OracleForms6iSourceLab\app\generated',
    [string]$CredentialPath = 'C:\OracleForms6iSourceLab\connection\meridian.dpapi'
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MeridianFormsNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ContextAttributes { public uint Mask; public IntPtr Data, Allocate, Free, Reallocate; }
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, EntryPoint="d2fctxcr_Create")]
    public static extern int CreateContext(out IntPtr context, ref ContextAttributes attributes);
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, EntryPoint="d2fctxde_Destroy")]
    public static extern int DestroyContext(IntPtr context);
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi, EntryPoint="d2fobcr_Create")]
    public static extern int CreateObject(IntPtr context, IntPtr parent, out IntPtr value, string name, ushort type);
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, EntryPoint="d2fobsn_SetNumProp")]
    public static extern int SetNumber(IntPtr context, IntPtr value, ushort property, uint number);
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, EntryPoint="d2fobsb_SetBoolProp")]
    public static extern int SetBoolean(IntPtr context, IntPtr value, ushort property, int enabled);
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi, EntryPoint="d2fobst_SetTextProp")]
    public static extern int SetText(IntPtr context, IntPtr value, ushort property, string text);
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi, EntryPoint="d2fitmile_InsertListElem")]
    public static extern int InsertListElement(IntPtr context, IntPtr item, uint index, string label, string value);
    [DllImport("ifd2f60.dll", CallingConvention=CallingConvention.Cdecl, CharSet=CharSet.Ansi, EntryPoint="d2ffmdsv_Save")]
    public static extern int Save(IntPtr context, IntPtr module, string path, int database);
}
'@
if ($Action -eq 'Validate') { 'Native Forms interop definitions compile.'; return }
if ([Environment]::Is64BitProcess) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
    $info.Arguments = '-NoProfile -File "' + $PSCommandPath + '" -OracleHome "' + $OracleHome + '" -OutputDirectory "' + $OutputDirectory + '" -CredentialPath "' + $CredentialPath + '"'
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $child = New-Object Diagnostics.Process
    $child.StartInfo = $info
    [void]$child.Start()
    $output = $child.StandardOutput.ReadToEndAsync()
    $errors = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(90000)) { $child.Kill(); $child.WaitForExit(); throw 'Native form build timed out.' }
    $output.GetAwaiter().GetResult()
    $errors.GetAwaiter().GetResult()
    if ($child.ExitCode -ne 0) { throw ('Native form build failed: ' + $child.ExitCode) }
    return
}
$env:PATH = (Join-Path $OracleHome 'bin') + ';' + $env:PATH
$env:ORACLE_HOME = $OracleHome
$header = [regex]::Replace((Get-Content (Join-Path $OracleHome 'FORMS60\API\D2FDEF.H') -Raw), '(?s)/\*.*?\*/', '')
$codes = @{}
foreach ($match in [regex]::Matches($header, '(?m)^\s*#define\s+(D2F\w+)\s+(\d+)\s*$')) {
    $codes[$match.Groups[1].Value] = [uint32]$match.Groups[2].Value
}
function Get-Code([string]$Name) {
    if (-not $codes.ContainsKey($Name)) { throw ('Missing installed API constant: ' + $Name) }
    return $codes[$Name]
}
function Assert-Status([int]$Status, [string]$Operation) {
    if ($Status -ne 0) { throw ($Operation + ' returned Forms API status ' + $Status) }
}
function New-NativeObject([IntPtr]$Parent, [string]$Name, [string]$Type) {
    $value = [IntPtr]::Zero
    Assert-Status ([MeridianFormsNative]::CreateObject($context, $Parent, [ref]$value, $Name, (Get-Code $Type))) ('Create ' + $Name)
    return $value
}
function Set-Number([IntPtr]$Object, [string]$Property, [uint32]$Value) {
    Assert-Status ([MeridianFormsNative]::SetNumber($context, $Object, (Get-Code $Property), $Value)) $Property
}
function Set-Text([IntPtr]$Object, [string]$Property, [string]$Value) {
    Assert-Status ([MeridianFormsNative]::SetText($context, $Object, (Get-Code $Property), $Value)) $Property
}
function Set-Boolean([IntPtr]$Object, [string]$Property, [bool]$Value) {
    Assert-Status ([MeridianFormsNative]::SetBoolean($context, $Object, (Get-Code $Property), [int]$Value)) $Property
}
function Add-Trigger([IntPtr]$Parent, [string]$Name, [string]$Body) {
    $trigger = New-NativeObject $Parent $Name 'D2FFO_TRIGGER'
    Set-Text $trigger 'D2FP_TRG_TXT' ($Body.Replace("`r`n", "`n"))
}
function Add-Field([string]$Name, [string]$Prompt, [int]$Left, [int]$Top, [int]$Width, [string]$Type = 'D2FC_ITTY_TI', [bool]$Numeric = $false) {
    $field = New-NativeObject $block $Name 'D2FFO_ITEM'
    Set-Boolean $field 'D2FP_DB_ITM' $false
    Set-Text $field 'D2FP_CNV_NAM' 'ORDER_CANVAS'
    Set-Text $field 'D2FP_PRMPT' $Prompt
    Set-Number $field 'D2FP_ITM_TYP' (Get-Code $Type)
    Set-Number $field 'D2FP_X_POS' $Left
    Set-Number $field 'D2FP_Y_POS' $Top
    Set-Number $field 'D2FP_WIDTH' $Width
    Set-Number $field 'D2FP_HEIGHT' 26
    Set-Number $field 'D2FP_MAX_LEN' 240
    Set-Number $field 'D2FP_DAT_TYP' (Get-Code $(if ($Numeric) { 'D2FC_DATY_NUMBER' } else { 'D2FC_DATY_CHAR' }))
    if ($Type -eq 'D2FC_ITTY_LS') {
        Assert-Status ([MeridianFormsNative]::InsertListElement($context, $field, 1, 'Select', '0')) ('Initialize list ' + $Name)
    }
    return $field
}
function Add-Button([string]$Name, [string]$Label, [int]$Left, [string]$Body) {
    $button = Add-Field $Name '' $Left 250 130 'D2FC_ITTY_PB'
    Set-Text $button 'D2FP_LABEL' $Label
    Set-Number $button 'D2FP_HEIGHT' 34
    Add-Trigger $button 'WHEN-BUTTON-PRESSED' $Body
}
$context = [IntPtr]::Zero
try {
    $attributes = New-Object MeridianFormsNative+ContextAttributes
    Assert-Status ([MeridianFormsNative]::CreateContext([ref]$context, [ref]$attributes)) 'Create context'
    $module = New-NativeObject ([IntPtr]::Zero) 'MRD_ORDER_ENTRY' 'D2FFO_FORM_MODULE'
    Set-Text $module 'D2FP_TITLE' 'Meridian Order Entry'
    Set-Number $module 'D2FP_COORD_SYS' (Get-Code 'D2FC_COSY_REAL')
    Set-Number $module 'D2FP_REAL_UNIT' (Get-Code 'D2FC_REUN_PIXEL')
    $window = New-NativeObject $module 'ORDER_WINDOW' 'D2FFO_WINDOW'
    Set-Text $window 'D2FP_TITLE' 'Meridian Order Entry'
    Set-Number $window 'D2FP_WIDTH' 720
    Set-Number $window 'D2FP_HEIGHT' 390
    $canvas = New-NativeObject $module 'ORDER_CANVAS' 'D2FFO_CANVAS'
    Set-Text $canvas 'D2FP_WND_NAM' 'ORDER_WINDOW'
        Set-Number $canvas 'D2FP_WIDTH' 720
        Set-Number $canvas 'D2FP_HEIGHT' 390
    $block = New-NativeObject $module 'ORDER_ENTRY' 'D2FFO_BLOCK'
    Set-Boolean $block 'D2FP_DB_BLK' $false
        [void](Add-Field 'HEADING' '' 30 20 650 'D2FC_ITTY_DI')
        [void](Add-Field 'CUSTOMER_ID' 'Customer' 160 70 490 'D2FC_ITTY_LS')
        $product = Add-Field 'PRODUCT_ID' 'Product' 160 114 490 'D2FC_ITTY_LS'
        $quantity = Add-Field 'QUANTITY' 'Quantity' 160 158 100 'D2FC_ITTY_TI' $true
        [void](Add-Field 'UNIT_PRICE' 'Unit price' 430 158 160 'D2FC_ITTY_DI' $true)
        [void](Add-Field 'TOTAL_AMOUNT' 'Total' 160 202 160 'D2FC_ITTY_DI' $true)
        [void](Add-Field 'ORDER_ID' 'Order ID' 160 310 180 'D2FC_ITTY_DI' $true)
        [void](Add-Field 'STATUS_MESSAGE' '' 30 360 650 'D2FC_ITTY_DI')
        $pending = Add-Field 'PENDING_FLAG' '' 30 350 20
        Set-Boolean $pending 'D2FP_VISIBLE' $false
        Set-Boolean $pending 'D2FP_KBRD_NAVIGABLE' $false
        $recalculate = @'
BEGIN
    IF :ORDER_ENTRY.PRODUCT_ID IS NOT NULL THEN
        SELECT UNIT_PRICE INTO :ORDER_ENTRY.UNIT_PRICE FROM PRODUCTS WHERE PRODUCT_ID = TO_NUMBER(:ORDER_ENTRY.PRODUCT_ID);
        :ORDER_ENTRY.TOTAL_AMOUNT := :ORDER_ENTRY.UNIT_PRICE * NVL(:ORDER_ENTRY.QUANTITY, 0);
    END IF;
EXCEPTION WHEN NO_DATA_FOUND THEN
    MESSAGE('Product is unavailable.'); RAISE FORM_TRIGGER_FAILURE;
END;
'@
        Add-Trigger $product 'WHEN-LIST-CHANGED' $recalculate
        Add-Trigger $quantity 'WHEN-VALIDATE-ITEM' @'
BEGIN
    IF :ORDER_ENTRY.QUANTITY IS NULL OR :ORDER_ENTRY.QUANTITY <= 0 OR :ORDER_ENTRY.QUANTITY <> TRUNC(:ORDER_ENTRY.QUANTITY) THEN
        MESSAGE('Quantity must be a positive whole number.'); RAISE FORM_TRIGGER_FAILURE;
    END IF;
    :ORDER_ENTRY.TOTAL_AMOUNT := :ORDER_ENTRY.UNIT_PRICE * :ORDER_ENTRY.QUANTITY;
END;
'@
        Add-Button 'CREATE_ORDER' 'Create Draft' 160 @'
DECLARE new_order NUMBER; customer_count NUMBER;
BEGIN
    IF :ORDER_ENTRY.PENDING_FLAG = 'Y' THEN MESSAGE('Save or cancel the pending order.'); RAISE FORM_TRIGGER_FAILURE; END IF;
    IF :ORDER_ENTRY.CUSTOMER_ID IS NULL OR :ORDER_ENTRY.PRODUCT_ID IS NULL OR NVL(:ORDER_ENTRY.QUANTITY,0) <= 0 OR :ORDER_ENTRY.QUANTITY <> TRUNC(:ORDER_ENTRY.QUANTITY) THEN
        MESSAGE('Select a customer and product, and enter a positive whole quantity.'); RAISE FORM_TRIGGER_FAILURE;
    END IF;
    SELECT COUNT(*) INTO customer_count FROM CUSTOMERS WHERE CUSTOMER_ID=TO_NUMBER(:ORDER_ENTRY.CUSTOMER_ID);
    IF customer_count <> 1 THEN MESSAGE('Customer is unavailable.'); RAISE FORM_TRIGGER_FAILURE; END IF;
    PLACE_ORDER(TO_NUMBER(:ORDER_ENTRY.CUSTOMER_ID),TO_NUMBER(:ORDER_ENTRY.PRODUCT_ID),:ORDER_ENTRY.QUANTITY,new_order);
    :ORDER_ENTRY.ORDER_ID := new_order;
    :ORDER_ENTRY.PENDING_FLAG := 'Y';
    :ORDER_ENTRY.STATUS_MESSAGE := 'Draft order ' || TO_CHAR(new_order) || ' - not saved';
EXCEPTION WHEN FORM_TRIGGER_FAILURE THEN RAISE; WHEN OTHERS THEN
    FORMS_DDL('ROLLBACK'); :ORDER_ENTRY.PENDING_FLAG := 'N'; MESSAGE(SQLERRM); RAISE FORM_TRIGGER_FAILURE;
END;
'@
        Add-Button 'SAVE_ORDER' 'Save' 310 @'
BEGIN
    IF :ORDER_ENTRY.PENDING_FLAG <> 'Y' THEN MESSAGE('No pending order.'); RAISE FORM_TRIGGER_FAILURE; END IF;
    FORMS_DDL('COMMIT');
    IF NOT FORM_SUCCESS THEN MESSAGE('Order could not be saved.'); RAISE FORM_TRIGGER_FAILURE; END IF;
    :ORDER_ENTRY.PENDING_FLAG := 'N';
    :ORDER_ENTRY.STATUS_MESSAGE := 'Order ' || TO_CHAR(:ORDER_ENTRY.ORDER_ID) || ' saved';
END;
'@
        Add-Button 'CANCEL_ORDER' 'Cancel Draft' 460 @'
BEGIN
    IF :ORDER_ENTRY.PENDING_FLAG <> 'Y' THEN MESSAGE('No pending order.'); RAISE FORM_TRIGGER_FAILURE; END IF;
    FORMS_DDL('ROLLBACK');
    :ORDER_ENTRY.PENDING_FLAG := 'N'; :ORDER_ENTRY.ORDER_ID := NULL;
    :ORDER_ENTRY.STATUS_MESSAGE := 'Draft cancelled';
END;
'@
        Add-Trigger $module 'WHEN-NEW-FORM-INSTANCE' @'
DECLARE customer_group RECORDGROUP; product_group RECORDGROUP; result_code NUMBER;
BEGIN
    SET_WINDOW_PROPERTY(FORMS_MDI_WINDOW, WINDOW_STATE, MAXIMIZE);
    :ORDER_ENTRY.HEADING := 'MERIDIAN ORDER ENTRY'; :ORDER_ENTRY.PENDING_FLAG := 'N'; :ORDER_ENTRY.QUANTITY := 1;
    customer_group := CREATE_GROUP_FROM_QUERY('CUSTOMER_CHOICES','SELECT CUSTOMER_NAME CHOICE_LABEL, TO_CHAR(CUSTOMER_ID) CHOICE_VALUE FROM CUSTOMERS ORDER BY CUSTOMER_NAME');
    result_code := POPULATE_GROUP(customer_group);
    IF result_code <> 0 THEN MESSAGE('Could not load customers.'); RAISE FORM_TRIGGER_FAILURE; END IF;
    POPULATE_LIST('ORDER_ENTRY.CUSTOMER_ID',customer_group);
    product_group := CREATE_GROUP_FROM_QUERY('PRODUCT_CHOICES','SELECT PRODUCT_CODE || '' - '' || DESCRIPTION CHOICE_LABEL, TO_CHAR(PRODUCT_ID) CHOICE_VALUE FROM PRODUCTS ORDER BY PRODUCT_CODE');
    result_code := POPULATE_GROUP(product_group);
    IF result_code <> 0 THEN MESSAGE('Could not load products.'); RAISE FORM_TRIGGER_FAILURE; END IF;
    POPULATE_LIST('ORDER_ENTRY.PRODUCT_ID',product_group);
    IF GET_GROUP_ROW_COUNT(customer_group)>0 THEN :ORDER_ENTRY.CUSTOMER_ID := GET_GROUP_CHAR_CELL('CUSTOMER_CHOICES.CHOICE_VALUE',1); END IF;
    IF GET_GROUP_ROW_COUNT(product_group)>0 THEN
        :ORDER_ENTRY.PRODUCT_ID := GET_GROUP_CHAR_CELL('PRODUCT_CHOICES.CHOICE_VALUE',1);
        SELECT UNIT_PRICE INTO :ORDER_ENTRY.UNIT_PRICE FROM PRODUCTS WHERE PRODUCT_ID=TO_NUMBER(:ORDER_ENTRY.PRODUCT_ID);
        :ORDER_ENTRY.TOTAL_AMOUNT := :ORDER_ENTRY.UNIT_PRICE;
    END IF;
    :ORDER_ENTRY.STATUS_MESSAGE := 'Connected';
    GO_ITEM('ORDER_ENTRY.CUSTOMER_ID');
END;
'@
    Set-Text $module 'D2FP_FRST_NAVIGATION_BLK_NAM' 'ORDER_ENTRY'
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $path = Join-Path $OutputDirectory 'MRD_ORDER_ENTRY.fmb'
    if (Test-Path $path) { Copy-Item $path ($path + '.before-' + (Get-Date -Format yyyyMMdd-HHmmss)) }
    Assert-Status ([MeridianFormsNative]::Save($context, $module, $path, 0)) 'Save form'
    'OFM-NATIVE-FORM-CREATED'
    'SavedPath=' + $path
    'SavedBytes=' + (Get-Item $path).Length
} finally {
    if ($context -ne [IntPtr]::Zero) { [void][MeridianFormsNative]::DestroyContext($context) }
}
$compileInfo = New-Object Diagnostics.ProcessStartInfo
$compileInfo.FileName = Join-Path $OracleHome 'bin\ifcmp60.exe'
$compileInfo.WorkingDirectory = $OutputDirectory
$compileInfo.Arguments = 'module=' + $path + ' module_type=form logon=no batch=yes compile_all=yes'
$password = $null
if (Test-Path $CredentialPath) {
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($CredentialPath), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    try { $credential = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json } finally { [Array]::Clear($plain, 0, $plain.Length) }
    $password = $credential.Password
    if ($credential.User -cne 'MERIDIAN' -or [string]::IsNullOrEmpty($password) -or $password -match '[\s"@/\\]') { throw 'Unsupported credential format.' }
    $compileInfo.EnvironmentVariables['TNS_ADMIN'] = Split-Path $CredentialPath
    $compileInfo.Arguments = 'module=' + $path + ' userid=MERIDIAN/' + $password + '@OFM9I module_type=form logon=yes batch=yes compile_all=yes'
}
$compileInfo.UseShellExecute = $false
$compileInfo.RedirectStandardOutput = $true
$compileInfo.RedirectStandardError = $true
$compiler = New-Object Diagnostics.Process
$compiler.StartInfo = $compileInfo
[void]$compiler.Start()
$output = $compiler.StandardOutput.ReadToEndAsync()
$errors = $compiler.StandardError.ReadToEndAsync()
if (-not $compiler.WaitForExit(30000)) { $compiler.Kill(); $compiler.WaitForExit(); throw 'Compiler timed out.' }
$stdout = $output.GetAwaiter().GetResult()
$stderr = $errors.GetAwaiter().GetResult()
if ($password) { $stdout = $stdout.Replace($password, '[redacted]'); $stderr = $stderr.Replace($password, '[redacted]') }
$stdout
$stderr
'CompilerExit=' + $compiler.ExitCode
if (Test-Path ([IO.Path]::ChangeExtension($path, 'err'))) {
    $report = Get-Content ([IO.Path]::ChangeExtension($path, 'err')) -Raw
    if ($password) { $report = $report.Replace($password, '[redacted]') }
    Set-Content ([IO.Path]::ChangeExtension($path, 'err')) $report
    $report
}
$password = $null
$credential = $null
if ($compiler.ExitCode -ne 0) { throw 'Forms compilation failed.' }
'FmxBytes=' + (Get-Item ([IO.Path]::ChangeExtension($path, 'fmx'))).Length