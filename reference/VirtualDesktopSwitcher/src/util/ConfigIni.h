#pragma once

#include <string>

std::wstring GetAppDataDir();
std::wstring ReadIniString(const std::wstring &section, const std::wstring &key, const std::wstring &defaultVal);
int          ReadIniInt(const std::wstring &section, const std::wstring &key, int defaultVal);
float        ReadIniFloat(const std::wstring &section, const std::wstring &key, float defaultVal);
void         WriteIniString(const std::wstring &section, const std::wstring &key, const std::wstring &value);
void         WriteIniInt(const std::wstring &section, const std::wstring &key, int value);
void         WriteIniFloat(const std::wstring &section, const std::wstring &key, float value);
std::wstring EncodeSymbol(const std::wstring &sym);
std::wstring DecodeSymbol(const std::wstring &str);
std::wstring ReadIniSymbol(const std::wstring &section, const std::wstring &key, const std::wstring &defaultSym);
