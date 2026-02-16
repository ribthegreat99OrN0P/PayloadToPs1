Example folder contains some already converted ps1 scripts.

to use them use:

IEX (New-Object Net.WebClient).DownloadString('LOCATION')

Then to invoke function use:

Invoke-Function -Command "--arg1 --arg2 test"
