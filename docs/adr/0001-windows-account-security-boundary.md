# Use the Windows account as the security boundary

Protect the recorder against other local Windows accounts and lower-trust shared writable locations. Ordinary processes running as the recorder's account remain trusted to use the local control channel; isolating them would require a broader authorization design. The system-audio pipe has a narrower recipient rule: deliver audio only to the FFmpeg process launched for that capture session.
