MasterRelayVPN
==============

A no-setup local proxy for restricted networks.

How to use
----------

1. Run MasterRelayVPN.exe.
2. Wait a moment for the app to set itself up the first time.
3. Click Start.

Files
-----

Everything the app needs lives next to MasterRelayVPN.exe:

    MasterRelayVPN.exe       <-- the app
    core\MasterRelayCore.exe <-- relay engine (auto-started)
    data\config.json         <-- your settings
    data\cert\               <-- generated certificate
    data\logs\               <-- logs

Delete the data\ folder to reset everything.

Settings
--------

Click Settings if you need to:
  - paste your Apps Script ID / Worker URL
  - try a different SNI for your network
  - tune chunk size or fragment size
  - install the certificate manually

The app turns Windows' system proxy on while running and off when you Stop.
