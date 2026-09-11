"""TIA adapter boundary.

The concrete Siemens Openness adapter deliberately remains the .NET Framework 4.8
``EngineerPc.Tia.V19`` project: ``Siemens.Engineering.dll`` is a .NET assembly with no
Python binding. Python speaks to it through the existing framework-neutral JSON
stdin/stdout worker protocol, which keeps Siemens types out of this process exactly as
``AGENTS.md`` requires.
"""
