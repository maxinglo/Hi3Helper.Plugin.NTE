[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HostDirectory,
    [ValidateSet('en','zh-CN','zh-TW','ja','ko','de','fr','ru','es')][string[]] $Languages = @('zh-CN','de'),
    [switch] $NoBuild
)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
$HostDirectory=(Resolve-Path -LiteralPath $HostDirectory).Path
$reportDirectory=Join-Path $repository ('artifacts/research/login-ux-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$checks=[Collections.Generic.List[object]]::new()
$sessions=[Collections.Generic.List[string]]::new()
$captures=[Collections.Generic.List[string]]::new()
function Check([bool]$Passed,[string]$Name) {
    $checks.Add([pscustomobject]@{name=$script:caseTag+'/'+$Name;passed=$Passed})
    if(-not $Passed){throw "UX verification failed: $script:caseTag/$Name"}
}
function Invoke-Ui([string]$Action,[hashtable]$Fields=@{}) {
    & (Join-Path $PSScriptRoot 'Invoke-NteLoginUiVerification.ps1') -SessionDirectory $script:currentSession -Action $Action @Fields
}
function Nodes([string]$Role) { @($script:state.state.nodes | Where-Object role -eq $Role) }
function Node([string]$Role) {
    $items=@(Nodes $Role)
    if($items.Count -ne 1){throw "Expected exactly one visible control: $script:caseTag/$Role; found $($items.Count)"}
    $items[0]
}
function Act([string]$Action,[string]$Role,[hashtable]$Fields=@{}) {
    $control=Node $Role
    $script:state=Invoke-Ui $Action (@{Node=$control.id;Revision=$script:state.state.revision}+$Fields)
    if(-not $script:state.ok){throw "Control rejected: $Role / $($script:state.error)"}
}
function Scenario([hashtable]$Fields) {
    $script:state=Invoke-Ui scenario $Fields
    if(-not $script:state.ok){throw "Scenario rejected: $($script:state.error)"}
}
function Capture([string]$Name,[string]$Theme='night') {
    $script:state=Invoke-Ui capture @{Name=($script:caseTag.Replace('/','-')+'-'+$Name+'-'+$Theme);Theme=$Theme}
    $result=$script:state.state.capture
    Check ($script:state.ok -and (Test-Path -LiteralPath $result.path) -and -not $result.desktopCaptured -and -not $result.liveBackdropFidelity) ($Name+'/'+$Theme+'/capture')
    $png=[IO.File]::ReadAllBytes($result.path)
    Check ($png.Length -gt 10000 -and [Convert]::ToHexString($png[0..7]) -eq '89504E470D0A1A0A') ($Name+'/'+$Theme+'/png')
    $captures.Add($result.path)
}
$completed=$false; $built=$NoBuild.IsPresent
try {
    foreach($language in $Languages) {
        foreach($region in @('Global','Taiwan','CN')) {
            $script:caseTag=$region+'/'+$language
            Write-Host "Verifying $script:caseTag"
            $ui=& (Join-Path $PSScriptRoot 'Start-NteLoginUiVerification.ps1') -HostDirectory $HostDirectory -Region $region -Language $language -NoBuild:$built
            $built=$true; $script:currentSession=$ui.SessionDirectory; $sessions.Add($ui.SessionDirectory)
            $script:state=Invoke-Ui state
            Check ($state.ok -and $state.state.syntheticOnly -and -not $state.state.previewTaskFaulted) 'actual-published-preview'
            Check ($state.state.hostSha256 -eq (Get-FileHash (Join-Path $HostDirectory 'Nte.LoginHost.dll')).Hash) 'host-binding'
            Check ($state.state.page -eq 'Saved') 'saved-home'
            Check (-not $state.state.card.scrollbarVisible -and $state.state.card.scrollableHeight -le 1) 'saved-home-fits-without-scrollbar'
            Check (@(Nodes help).Count -eq 1) 'avatar-is-only-help-entry'
            Check ((Node help).x -gt $state.state.card.renderX+$state.state.card.renderWidth-20 -and (Node help).y -gt $state.state.card.renderY) 'avatar-peeks-from-panel-side'
            Check (@(Nodes remember).Count -eq 0 -and @(Nodes agreement).Count -eq 0 -and @(Nodes provider-google).Count -eq 0 -and @(Nodes provider-facebook).Count -eq 0) 'saved-home-only-restores'
            Check ((Node account-selector).kind -eq 'ListBox' -and (Node account-selector).choices.Count -eq 3) 'semantic-three-account-list'
            foreach($theme in @('night','day','sunset')) { Capture saved $theme }
            if($language -eq 'zh-CN') {
                foreach($initial in @($false,$true)) { foreach($outcome in @('success','cancel','failure')) {
                    $state=Invoke-Ui prompt-probe @{Outcome=$outcome;InitiallyTopmost=$initial}
                    $probe=$state.state.promptProbe
                    Check ($state.ok -and $probe.ownerPromoted -and $probe.childPromoted -and $probe.owned) "prompt-owner/$initial/$outcome"
                    Check ($probe.observed -eq $outcome -and $probe.ownerRestored -and $probe.originalStateRestored -and -not $probe.systemDialogUsed -and -not $probe.securityGestureRequested) "prompt-restore/$initial/$outcome"
                }}
            }
            if($region -ne 'CN') {
                foreach($variant in @('missing','legacy','legacy-profile')) {
                    Scenario @{ProfileVariant=$variant}
                    $choices=(Node account-selector).choices
                    Check ($choices.Count -eq 3 -and $choices[0] -ne $choices[1]) ($variant+'/accounts-remain-distinguishable')
                    if($variant -eq 'legacy-profile'){Check ($choices[0] -like '*Legacy label 1*' -and $choices[0] -like '*@example.com*') 'legacy-remark-kept-with-new-profile'}
                    if($language -eq 'zh-CN'){Capture $variant}
                }
                Scenario @{AccountCount=3}
            }
            $oldRevision=$state.state.revision; $oldNode=(Node account-selector).id
            Act select account-selector @{Index=1}
            Check ((Node account-selector).selectedIndex -eq 1) 'selection-event'
            $state=Invoke-Ui select @{Node=$oldNode;Revision=$oldRevision;Index=0}
            Check (-not $state.ok -and $state.error -eq 'stale-revision') 'stale-rejected'
            Act invoke manage-accounts
            Check (@(Nodes remark).Count -eq 1 -and @(Nodes submit).Count -eq 0) 'management-separate-from-sign-in'
            if($language -eq 'zh-CN'){Capture management}
            Act fill remark @{Value='Fixture renamed account'}
            Act key remark @{Key='Enter'}
            $list=Node account-selector
            Check ($list.selectedIndex -eq 1 -and $list.choices[1] -like '*Fixture renamed account*') 'enter-saves-remark-with-selection'
            $identity=if($region -ne 'CN'){'user2@example.com'}else{'138****0002'}
            Check ($list.choices[1] -like ('*'+$identity+'*')) 'remark-preserves-identification'
            Act invoke manage-accounts
            Act invoke remove-account
            Check (@(Nodes confirm-remove).Count -eq 1 -and @(Nodes account-selector).Count -eq 0) 'removal-needs-confirmation'
            if($language -eq 'zh-CN'){Capture remove-confirmation}
            Act invoke cancel-remove
            Act invoke cancel-edit
            Check ((Node account-selector).choices.Count -eq 3) 'cancel-removal-keeps-account'
            Act invoke manage-accounts
            Act invoke remove-account
            Act invoke confirm-remove
            Check ((Node account-selector).choices.Count -eq 2) 'confirmed-removal'
            Act invoke help
            Check ($state.state.page -eq 'Help' -and @(Nodes email).Count -eq 0 -and @(Nodes phone).Count -eq 0) 'visible-help'
            Check (@(Nodes official-launcher).Count -eq 1) 'shared-notice-official-choice'
            Act invoke official-launcher
            Check ($state.state.noticeConfirmation -and @(Nodes confirm-official-launcher).Count -eq 1) 'official-choice-confirmation'
            Capture official-confirmation
            Act invoke cancel-official-launcher
            Check (-not $state.state.noticeConfirmation -and $state.state.page -eq 'Help') 'official-choice-cancel'
            Act invoke submit
            Check ($state.state.page -eq 'Saved') 'help-return'
            Scenario @{FirstUseNotice=$true}
            Check ($state.state.mandatoryNotice -and $state.state.page -eq 'Help') 'first-use-shows-shared-notice'
            Act key submit @{Key='Escape'}
            Check ($state.state.mandatoryNotice -and $state.state.page -eq 'Help') 'escape-does-not-dismiss-first-use'
            Act invoke help
            Check ($state.state.mandatoryNotice -and $state.state.page -eq 'Help') 'avatar-does-not-dismiss-first-use'
            Act invoke official-launcher
            Act invoke cancel-official-launcher
            Check ($state.state.mandatoryNotice) 'cancel-official-keeps-first-use-pending'
            Capture first-use-notice
            Act invoke submit
            Check (-not $state.state.mandatoryNotice -and $state.state.page -eq 'Saved') 'explicit-understood-releases-first-use'
            Scenario @{AccountCount=1}
            Check ((Node account-selector).choices.Count -eq 1) 'single-account'
            Check (-not $state.state.card.scrollbarVisible -and $state.state.card.scrollableHeight -le 1) 'single-account-fits-without-scrollbar'
            Act invoke other-account
            if($region -eq 'Global') {
                Check ($state.state.page -eq 'Methods' -and @(Nodes provider-google).Count -eq 1 -and @(Nodes provider-facebook).Count -eq 1 -and @(Nodes email-login).Count -eq 1 -and @(Nodes submit).Count -eq 0) 'three-clear-new-login-methods'
                Check (@($state.state.nodes | Where-Object {$_.kind -eq 'Image' -and $_.asset -in @('google-g.png','facebook-primary.png')}).Count -eq 2) 'official-image-assets'
                Check (@($state.state.nodes | Where-Object {$_.kind -eq 'TextBlock' -and $_.text -match 'Google|Facebook' -and $_.fontFamily -ne 'Microsoft YaHei UI'}).Count -eq 0) 'provider-labels-use-one-ui-font'
                Check (@($state.state.nodes | Where-Object kind -eq 'CheckBox').Count -eq 2 -and @(Nodes terms).Count -eq 1 -and @(Nodes privacy).Count -eq 1) 'one-consent-block-with-two-links'
                foreach($theme in @('night','day','sunset')) { Capture methods $theme }
                $methodsHeight=$state.state.card.height
                Act invoke provider-google
                Check (-not [string]::IsNullOrWhiteSpace((Node status).text) -and -not (Node agreement).checked) 'missing-consent-explained'
                foreach($role in @('terms','privacy')) {
                    $link=Node $role; $state=Invoke-Ui invoke @{Node=$link.id;Revision=$state.state.revision}
                    Check (-not $state.ok -and $state.error -eq 'external-navigation-blocked') ($role+'/external-navigation-blocked')
                }
                Act toggle agreement @{Checked=$true}
                Act invoke email-login
                Check ($state.state.page -eq 'Email' -and @(Nodes email-password-mode).Count -eq 1 -and @(Nodes email-code-mode).Count -eq 1 -and (Node email-password-mode).checked -and @(Nodes secret).Count -eq 1 -and @(Nodes email-code).Count -eq 0) 'explicit-password-mode'
                Act fill email @{Value='fixture@example.com'}
                Act fill secret @{Value='fixture-password'}
                Act key email @{Key='Enter'}
                Check ($state.state.page -eq 'Email' -and (Node secret).secretCharacters -eq 16 -and $null -eq (Node secret).value) 'enter-routes-current-form-without-authentication'
                Capture email-password
                Act invoke email-code-mode
                Check ((Node email).value -eq 'fixture@example.com' -and (Node email-code-mode).checked -and @(Nodes secret).Count -eq 0 -and (Node email-code).secretCharacters -eq 0) 'mode-keeps-email-and-separates-secrets'
                Check ($state.state.modeSelector.animated -and $state.state.modeSelector.width -gt 0) 'mode-thumb-uses-animation'
                Start-Sleep -Milliseconds 220
                $state=Invoke-Ui state
                Check ([Math]::Abs($state.state.modeSelector.x-$state.state.modeSelector.width) -lt 1) 'mode-thumb-reaches-code-tab'
                Act fill email-code @{Value='123456'}
                Check ((Node email-code).secretCharacters -eq 6 -and $null -eq (Node email-code).value -and (Node email-code).kind -eq 'TextBox') 'code-visible-in-ui-redacted-in-report'
                Act invoke send-code
                Check (-not (Node send-code).enabled) 'preview-code-cooldown'
                $disabled=Node send-code; $state=Invoke-Ui invoke @{Node=$disabled.id;Revision=$state.state.revision}
                Check (-not $state.ok -and $state.error -eq 'unavailable-control') 'disabled-control-rejected'
                Capture email-code
                Act invoke email-password-mode
                Check ((Node secret).secretCharacters -eq 0 -and (Node email).value -eq 'fixture@example.com') 'password-cleared-between-modes'
                Act invoke email-code-mode
                Check (-not (Node send-code).enabled) 'mode-cannot-bypass-cooldown'
                Act key email @{Key='Escape'}
                Check ($state.state.page -eq 'Methods') 'escape-returns-to-methods'
                Check ([Math]::Abs($state.state.card.height-$methodsHeight) -le 1) 'email-escape-restores-methods-height'
                for($round=1;$round -le 3;$round++) {
                    Act invoke email-login
                    Check ($state.state.card.height -gt $methodsHeight+20) "email-round-$round-expands-card"
                    Act invoke back
                    Check ($state.state.page -eq 'Methods' -and [Math]::Abs($state.state.card.height-$methodsHeight) -le 1) "email-round-$round-back-restores-height"
                }
                Capture methods-returned
                Act invoke back
            } elseif($region -eq 'Taiwan') {
                Check ($state.state.page -eq 'Methods' -and @(Nodes provider-google).Count -eq 1 -and @(Nodes provider-facebook).Count -eq 1 -and @(Nodes phone-login).Count -eq 1 -and @(Nodes email-login).Count -eq 0) 'taiwan-three-enabled-login-methods'
                Check (@(Nodes terms).Count -eq 1 -and @(Nodes privacy).Count -eq 1) 'taiwan-publisher-agreements'
                foreach($theme in @('night','day','sunset')) { Capture methods $theme }
                Act invoke provider-google
                Check (-not [string]::IsNullOrWhiteSpace((Node status).text) -and -not (Node agreement).checked) 'taiwan-consent-required'
                Act toggle agreement @{Checked=$true}
                Act invoke provider-google
                Check ($state.state.page -eq 'Methods' -and -not $state.state.previewTaskFaulted) 'taiwan-preview-never-authenticates'
                Act invoke phone-login
                Check ($state.state.page -eq 'Phone' -and @(Nodes phone-country).Count -eq 1 -and @(Nodes phone).Count -eq 1 -and @(Nodes phone-code).Count -eq 1 -and (Node phone-code-mode).checked) 'taiwan-phone-sms-is-default'
                Check ((Node phone-country).choices.Count -eq 3 -and (Node phone-country).selectedIndex -eq 0) 'taiwan-supported-calling-codes'
                Check (@($state.state.nodes|Where-Object {$_.kind -eq 'TextBlock' -and $_.text -like '*+886*'}).Count -ge 1 -and @($state.state.nodes|Where-Object {$_.kind -eq 'TextBlock' -and $_.text -like '*NtePhoneCountry*'}).Count -eq 0) 'taiwan-calling-code-renders-label'
                Check (-not $state.state.card.scrollbarVisible -and $state.state.card.scrollableHeight -le 1) 'taiwan-phone-fits-without-scrollbar'
                Act fill phone @{Value='0910000000'}
                Act fill phone-code @{Value='123456'}
                Check ((Node phone-code).secretCharacters -eq 6 -and $null -eq (Node phone-code).value) 'taiwan-sms-code-redacted-in-report'
                foreach($theme in @('night','day','sunset')) { Capture phone-code $theme }
                Act invoke send-code
                Check (-not (Node send-code).enabled) 'taiwan-sms-cooldown'
                Act invoke phone-password-mode
                Check (@(Nodes secret).Count -eq 1 -and @(Nodes phone-code).Count -eq 0 -and (Node phone-password-mode).checked) 'taiwan-phone-password-separate'
                Act fill secret @{Value='fixture-password'}
                Capture phone-password
                Act invoke phone-code-mode
                Check ((Node phone-code).secretCharacters -eq 0 -and -not (Node send-code).enabled) 'taiwan-mode-change-clears-secret-keeps-cooldown'
                Act fill phone-code @{Value='654321'}
                Act select phone-country @{Index=1}
                Check ((Node phone-country).selectedIndex -eq 1 -and (Node phone-code).secretCharacters -eq 0 -and -not (Node send-code).enabled) 'taiwan-country-change-clears-code-and-keeps-cooldown'
                Act key phone @{Key='Escape'}
                Check ($state.state.page -eq 'Methods') 'taiwan-phone-back-to-methods'
                Act invoke back
            } else {
                Check ($state.state.page -eq 'Sms' -and @(Nodes phone).Count -eq 1 -and @(Nodes remember).Count -eq 1 -and (Node remember).kind -eq 'CheckBox') 'direct-sms-form-with-checkbox'
                Check (-not $state.state.card.scrollbarVisible -and $state.state.card.scrollableHeight -le 1) 'sms-fits-without-scrollbar'
                Act fill phone @{Value='13800000000'}
                Act fill secret @{Value='123456'}
                Check ((Node secret).kind -eq 'TextBox' -and (Node secret).secretCharacters -eq 6 -and $null -eq (Node secret).value) 'sms-code-visible-redacted-in-report'
                Act invoke send-code
                Check (-not (Node send-code).enabled) 'sms-preview-cooldown'
                Act invoke other-account
                Check ($state.state.page -eq 'Saved' -and @(Nodes remember).Count -eq 0) 'sms-back-to-saved'
                Act invoke other-account
                Check ((Node phone).value -eq '13800000000' -and (Node secret).secretCharacters -eq 6 -and -not (Node send-code).enabled) 'sms-draft-and-cooldown-preserved'
                foreach($theme in @('night','day','sunset')){Capture sms $theme}
                Act invoke other-account
            }
            Check ($state.state.page -eq 'Saved') 'new-account-return-preserves-record'
            Scenario @{AccountCount=16}
            Check ((Node account-selector).choices.Count -eq 16) 'many-account-capacity'
            Act select account-selector @{Index=15}
            Check ((Node account-selector).selectedIndex -eq 15) 'many-account-selection'
            Scenario @{AccountCount=3;Busy=$true;Notice='Fixture operation in progress'}
            if($region -ne 'CN'){Check (@(Nodes cancel-operation).Count -eq 1 -and @(Nodes account-selector).Count -eq 0) 'busy-offers-cancel'}
            else {
                $disabled=Node account-selector; $state=Invoke-Ui select @{Node=$disabled.id;Revision=$state.state.revision;Index=1}
                Check (-not $state.ok -and $state.error -eq 'unavailable-control') 'busy-disables-selection'
            }
            Scenario @{Busy=$false;Notice=''}
            $bad=Invoke-Ui capture @{Name='../outside'}
            Check (-not $bad.ok -and $bad.error -eq 'invalid-capture-name') 'capture-path-rejected'
            $null=Invoke-Ui resize @{Width=1280;Height=720}
            $deadline=[DateTime]::UtcNow.AddSeconds(8)
            do {$state=Invoke-Ui state; if($state.state.pixelWidth -eq 1280 -and $state.state.pixelHeight -eq 720){break}; Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
            Check ($state.state.pixelWidth -eq 1280 -and $state.state.pixelHeight -eq 720) '1280x720-size'
            if($language -eq 'zh-CN'){Capture compact}
            Scenario @{AccountCount=0}
            Check ($state.state.page -eq $(if($region -ne 'CN'){'Methods'}else{'Sms'}) -and @(Nodes account-selector).Count -eq 0) 'empty-goes-directly-to-new-sign-in'
            $state=Invoke-Ui close; Check $state.ok 'normal-close'
            $deadline=[DateTime]::UtcNow.AddSeconds(5)
            do {
                $previewAlive=Get-Process -Id $ui.VerificationPid -ErrorAction SilentlyContinue
                $fixtureAlive=Get-Process -Id $ui.FixturePid -ErrorAction SilentlyContinue
                if(-not $previewAlive -and -not $fixtureAlive){break}
                Start-Sleep -Milliseconds 100
            }while([DateTime]::UtcNow -lt $deadline)
            Check (-not $previewAlive -and -not $fixtureAlive) 'owned-processes-exited'
        }
    }
    $script:caseTag='switch-preview'
    $verification=Join-Path $repository 'tools/NteLoginUiVerification/bin/x64/Release/net10.0-windows/NteLoginUiVerification.exe'
    $switchDirectory=Join-Path $reportDirectory 'switch-preview'
    $switchOutput=& $verification switch-preview $HostDirectory $switchDirectory
    $switchExit=$LASTEXITCODE
    $switchReport=$switchOutput|ConvertFrom-Json
    Check ($switchExit -eq 0 -and $switchReport.passed -and $switchReport.nativeRecoveryPreview -and $switchReport.journalUnchanged) 'native-recovery-preview'
    Check ($switchReport.commandsBound -and (Test-Path -LiteralPath $switchReport.capture)) 'bound-and-rendered'
    $captures.Add($switchReport.capture)
    $script:caseTag='release'
    $forbidden=Join-Path $reportDirectory 'forbidden-package-fixture'
    New-Item -ItemType Directory -Path $forbidden | Out-Null
    [IO.File]::WriteAllText((Join-Path $forbidden 'NteLoginUiVerification.dll'),'synthetic forbidden asset')
    $rejected=$false
    try {& (Join-Path $PSScriptRoot 'Test-NtePackageContent.ps1') -PublishDirectory $forbidden | Out-Null}
    catch {$rejected=$_.Exception.Message -like '*research executable*'}
    Check $rejected 'verification-excluded'
    $completed=$true
}
finally {
    foreach($session in $sessions) {
        $metadata=Get-Content -LiteralPath (Join-Path $session 'session.json') -Raw | ConvertFrom-Json
        if(Get-Process -Id $metadata.VerificationPid -ErrorAction SilentlyContinue) {
            $script:currentSession=$session
            try {$null=Invoke-Ui close}catch{Write-Warning 'An owned preview did not respond to normal close.'}
        }
    }
    $report=[ordered]@{passed=($completed -and @($checks|Where-Object {-not $_.passed}).Count -eq 0);checks=$checks;sessions=$sessions;captures=$captures;hostDirectory=$HostDirectory;hostSha256=(Get-FileHash (Join-Path $HostDirectory 'Nte.LoginHost.dll')).Hash;authenticationRequests=0;syntheticOnly=$true;desktopCaptured=$false;realHelloGesture='not-observed';realGameEntry='not-observed'}
    $report|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $reportDirectory 'result.json')
}
[pscustomobject]@{Passed=$report.passed;Checks=$checks.Count;Report=(Join-Path $reportDirectory 'result.json');Sessions=$sessions.Count;Captures=$captures.Count;AuthenticationRequests=0}
