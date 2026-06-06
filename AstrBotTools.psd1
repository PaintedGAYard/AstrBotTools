@{
    RootModule           = 'AstrBotTools.dll'
    ModuleVersion        = '1.0.0'
    GUID                 = 'a3f8c2e1-5b4d-4e7f-9a1c-8d6b2e0f3a7c'
    Author               = 'AstrBot User'
    CompanyName          = ''
    Copyright            = '(c) 2026. All rights reserved.'
    Description          = 'AstrBot 知识库管理工具 - 批量上传文件、查询知识库'

    # Cmdlets to export
    FunctionsToExport    = @()
    CmdletsToExport      = @(
        'Get-AstrBotKnowledgeBaseList',
        'Add-AstrBotKnowledgeBaseDocument'
    )
    AliasesToExport      = @(
        'Get-KBList',
        'Add-KBDoc'
    )

    # Format file for default display
    FormatsToProcess     = @('AstrBotTools.format.ps1xml')

    # Compatible PowerShell editions
    PowerShellVersion    = '7.0'

    # Required modules
    RequiredAssemblies   = @('AstrBotTools.dll')

    # Private data
    PrivateData = @{
        PSData = @{
            Tags         = @('AstrBot', 'KnowledgeBase', 'Upload')
            ProjectUri   = 'https://github.com/Soulter/AstrBot'
        }
    }
}
