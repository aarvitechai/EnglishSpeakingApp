@echo off
title NeuroTara tunnel - keep this window open
echo Connecting https://interview.neurotara.com to this PC...
echo Keep this window open. Closing it takes the site offline.
echo.
"C:\Program Files (x86)\cloudflared\cloudflared.exe" tunnel run neurotara-interview
echo.
echo The tunnel stopped. Copy the messages above and send them to Claude.
pause
