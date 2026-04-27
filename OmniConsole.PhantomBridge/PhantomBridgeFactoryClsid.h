#pragma once

// ============================================================================
// Full Trust COM Server 的 CLSID
//
// 此 GUID 必須同步於三處：
//   1. 本檔案（C++ server 端 CoRegisterClassObject 使用）
//   2. PhantomLink 的 Package.appxmanifest（<com:ComServer> 宣告）
//   3. PhantomLink C# 的 CoCreateInstance 呼叫（PInvoke CLSID 引數）
//
// 變更此 GUID 時必須三處一致；否則 Widget 呼叫會得到 REGDB_E_CLASSNOTREG (0x80040154)。
// ============================================================================

// {0370C27A-B39D-4B74-B20A-639B49026B14}
constexpr CLSID CLSID_PhantomBridgeFactory =
{ 0x0370c27a, 0xb39d, 0x4b74, { 0xb2, 0x0a, 0x63, 0x9b, 0x49, 0x02, 0x6b, 0x14 } };
