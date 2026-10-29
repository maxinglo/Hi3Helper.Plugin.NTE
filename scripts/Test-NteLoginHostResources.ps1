[CmdletBinding()]
param([Parameter(Mandatory)][string] $HostDirectory)
$ErrorActionPreference='Stop'
$directory=(Resolve-Path -LiteralPath $HostDirectory).Path
$assembly=[Reflection.Assembly]::LoadFile((Join-Path $directory 'Nte.LoginHost.dll'))
$manager=[Resources.ResourceManager]::new('Nte.LoginHost.Localization.Strings',$assembly)
$baseline=$manager.GetResourceSet([Globalization.CultureInfo]::InvariantCulture,$true,$false)
$keys=@($baseline.GetEnumerator() | ForEach-Object Key | Sort-Object)
$rows=@(foreach($culture in @('en','zh-CN','zh-TW','ja','ko','de','fr','ru','es')){
    $info=[Globalization.CultureInfo]::GetCultureInfo($culture)
    $resourceCulture=if($culture -eq 'en'){[Globalization.CultureInfo]::InvariantCulture}else{$info}
    $explicitResources=$manager.GetResourceSet($resourceCulture,$true,$false)
    if($null -eq $explicitResources){throw "Missing explicit resource catalog: $culture"}
    foreach($key in $keys){
        $value=$explicitResources.GetString($key)
        if([string]::IsNullOrWhiteSpace($value)){throw "Missing login resource: $culture / $key"}
        $expected=@([regex]::Matches($baseline.GetString($key),'\{\d+\}') | ForEach-Object Value | Sort-Object)
        $actual=@([regex]::Matches($value,'\{\d+\}') | ForEach-Object Value | Sort-Object)
        if(($expected -join ',') -ne ($actual -join ',')){throw "Resource format placeholders differ: $culture / $key"}
    }
    [pscustomobject]@{Culture=$culture;Title=$manager.GetString('GlobalTitle',$info);ExplicitKeys=$keys.Count;Passed=$true}
})
$rows
