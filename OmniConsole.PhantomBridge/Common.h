#pragma once

#include <memory>
#include <softpub.h>

#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "wintrust.lib")

namespace Common
{
    constexpr BYTE kExpectedKeyHash[32] = {
        0x47, 0x9C, 0xD6, 0x58, 0xBE, 0x59, 0x73, 0x5B,
        0x22, 0xF7, 0xB7, 0x78, 0xFD, 0xC9, 0x79, 0xD2,
        0x40, 0xC8, 0x60, 0x99, 0x09, 0x75, 0xA4, 0xC2,
        0xE5, 0x78, 0x26, 0x61, 0x34, 0xCD, 0x47, 0x27
    };

    inline bool KeyHashMatches(PCCERT_CONTEXT cert) noexcept
    {
        if (cert == nullptr) return false;

        BYTE hash[32] = {};
        DWORD hashLen = sizeof(hash);
        if (!::CryptHashCertificate2(
                BCRYPT_SHA256_ALGORITHM, 0, nullptr,
                cert->pCertInfo->SubjectPublicKeyInfo.PublicKey.pbData,
                cert->pCertInfo->SubjectPublicKeyInfo.PublicKey.cbData,
                hash, &hashLen))
        {
            return false;
        }
        if (hashLen != sizeof(kExpectedKeyHash)) return false;

        bool equal = true;
        for (size_t i = 0; i < sizeof(hash); ++i)
        {
            if (hash[i] != kExpectedKeyHash[i]) equal = false;
        }
        return equal;
    }

    inline bool CheckPath(const wchar_t* dllPath) noexcept
    {
        if (dllPath == nullptr) return false;

        HCERTSTORE store = nullptr;
        HCRYPTMSG msg = nullptr;
        bool ok = false;

        if (!::CryptQueryObject(
                CERT_QUERY_OBJECT_FILE, dllPath,
                CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
                CERT_QUERY_FORMAT_FLAG_BINARY, 0,
                nullptr, nullptr, nullptr, &store, &msg, nullptr))
        {
            return false;
        }

        DWORD signerInfoLen = 0;
        if (::CryptMsgGetParam(msg, CMSG_SIGNER_CERT_INFO_PARAM, 0, nullptr, &signerInfoLen)
            && signerInfoLen > 0)
        {
            auto signerInfoBuf = std::make_unique<BYTE[]>(signerInfoLen);
            if (::CryptMsgGetParam(msg, CMSG_SIGNER_CERT_INFO_PARAM, 0,
                    signerInfoBuf.get(), &signerInfoLen))
            {
                auto* signerInfo = reinterpret_cast<CERT_INFO*>(signerInfoBuf.get());
                PCCERT_CONTEXT signerCert = ::CertFindCertificateInStore(
                    store, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0,
                    CERT_FIND_SUBJECT_CERT, signerInfo, nullptr);
                if (signerCert != nullptr)
                {
                    ok = KeyHashMatches(signerCert);
                    ::CertFreeCertificateContext(signerCert);
                }
            }
        }

        if (msg != nullptr) ::CryptMsgClose(msg);
        if (store != nullptr) ::CertCloseStore(store, 0);
        return ok;
    }

    inline bool ContentIntact(const wchar_t* filePath) noexcept
    {
        WINTRUST_FILE_INFO fileInfo = {};
        fileInfo.cbStruct = sizeof(fileInfo);
        fileInfo.pcwszFilePath = filePath;

        GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        WINTRUST_DATA data = {};
        data.cbStruct = sizeof(data);
        data.dwUIChoice = WTD_UI_NONE;
        data.fdwRevocationChecks = WTD_REVOKE_NONE;
        data.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;
        data.dwUnionChoice = WTD_CHOICE_FILE;
        data.pFile = &fileInfo;
        data.dwStateAction = WTD_STATEACTION_VERIFY;

        LONG status = ::WinVerifyTrust(nullptr, &action, &data);

        data.dwStateAction = WTD_STATEACTION_CLOSE;
        ::WinVerifyTrust(nullptr, &action, &data);

        switch (static_cast<DWORD>(status))
        {
            case ERROR_SUCCESS:
            case static_cast<DWORD>(CERT_E_UNTRUSTEDROOT):
            case static_cast<DWORD>(CERT_E_UNTRUSTEDTESTROOT):
            case static_cast<DWORD>(CERT_E_EXPIRED):
                return true;
            default:
                return false;
        }
    }

    inline HMODULE Load(const wchar_t* dllPath) noexcept
    {
        if (dllPath == nullptr) return nullptr;

        HANDLE lock = ::CreateFileW(dllPath, GENERIC_READ, FILE_SHARE_READ, nullptr,
                                    OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (lock == INVALID_HANDLE_VALUE) return nullptr;

        HMODULE result = nullptr;
        if (ContentIntact(dllPath) && CheckPath(dllPath))
            result = ::LoadLibraryExW(dllPath, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);

        ::CloseHandle(lock);
        return result;
    }
}
