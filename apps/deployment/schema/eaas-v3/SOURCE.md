# EaaS v3 data dictionary — provenance

`GDS_ComplianceIQ_Data_Dictionary_v3.csv` is a verbatim copy of
`EaaS/GDS_ComplianceIQ_Data_Dictionary_v3.csv` from
[toby67maro/Documents](https://github.com/toby67maro/Documents/tree/f10e83b06f6a285365b60c1bc670a48c91e28b48/EaaS)
at commit `f10e83b06f6a285365b60c1bc670a48c91e28b48`.

- SHA-256: `4694b1dd295794a7ee34f694db20708ae2c6f6eac88f5d8084a2da2db16411d5`
- Format: pipe-delimited, UTF-8 with BOM, one row per column.

It is the input to `build/scripts/eaas-schema/generate_eaas_v3_schema.py`, which produces the
`V0001__eaas_v3_baseline.sql` scripts embedded in `EngineeringWorkspace.Infrastructure`. Do not edit
the CSV here; update it in the Documents repository, copy it again, regenerate, and record the new
commit and hash above.
