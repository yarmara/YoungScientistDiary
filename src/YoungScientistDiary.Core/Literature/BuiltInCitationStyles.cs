// SPDX-License-Identifier: Apache-2.0
namespace YoungScientistDiary.Core;

/// <summary>Original templates, not copies of journal styles or claims of ГОСТ compliance.</summary>
internal static class BuiltInCitationStyles
{
    public static readonly string[] All = [RussianNumeric, AuthorYear, EnglishNumeric];
    public const string RussianNumeric = """
<?xml version="1.0" encoding="utf-8"?>
<style xmlns="http://purl.org/net/xbiblio/csl" version="1.0" class="in-text" default-locale="ru-RU">
  <info><title>Русский числовой — базовый шаблон</title><id>urn:ysd:style:ru-numeric-v1</id><rights license="https://www.apache.org/licenses/LICENSE-2.0">Apache-2.0; оригинальный шаблон, не нормативный ГОСТ</rights></info>
  <macro name="authors"><names variable="author"><name name-as-sort-order="all" sort-separator=" " initialize-with=". " delimiter=", " et-al-min="7" et-al-use-first="3"/><et-al font-style="italic"/></names></macro>
  <macro name="source">
    <choose>
      <if type="article-journal paper-conference chapter" match="any">
        <group delimiter=". "><text variable="container-title" font-style="italic"/><date variable="issued"><date-part name="year"/></date><group delimiter=", "><text variable="volume" prefix="Т. "/><text variable="issue" prefix="№ "/></group><text variable="page" prefix="С. "/></group>
      </if>
      <else><group delimiter=", "><group delimiter=": "><text variable="publisher-place"/><text variable="publisher"/></group><date variable="issued"><date-part name="year"/></date></group></else>
    </choose>
  </macro>
  <bibliography hanging-indent="true" entry-spacing="1" line-spacing="1">
    <layout suffix="."><text variable="citation-number" suffix=". "/><group delimiter=". "><text macro="authors"/><text variable="title"/><text macro="source"/><choose><if variable="DOI"><text variable="DOI" prefix="DOI: "/></if><else><group delimiter=" "><text variable="URL" prefix="URL: "/><date variable="accessed" prefix="(дата обращения: " suffix=")" delimiter="."><date-part name="day" form="numeric-leading-zeros"/><date-part name="month" form="numeric-leading-zeros"/><date-part name="year"/></date></group></else></choose></group></layout>
  </bibliography>
</style>
""";
    public const string AuthorYear = """
<?xml version="1.0" encoding="utf-8"?>
<style xmlns="http://purl.org/net/xbiblio/csl" version="1.0" class="in-text" default-locale="ru-RU">
  <info><title>Автор — год — базовый шаблон</title><id>urn:ysd:style:author-year-v1</id><rights license="https://www.apache.org/licenses/LICENSE-2.0">Apache-2.0; оригинальный шаблон</rights></info>
  <macro name="author"><names variable="author"><name name-as-sort-order="all" sort-separator=", " initialize-with=". " delimiter=", " and="text"/><substitute><text variable="title"/></substitute></names></macro>
  <macro name="year"><choose><if variable="issued"><date variable="issued"><date-part name="year"/></date></if><else><text term="no date"/></else></choose></macro>
  <bibliography hanging-indent="true" entry-spacing="1"><sort><key variable="author"/><key variable="issued"/><key variable="title"/></sort>
    <layout suffix="."><text macro="author" suffix=" "/><text macro="year" prefix="(" suffix="). "/><group delimiter=". "><text variable="title"/><group delimiter=", "><text variable="container-title" font-style="italic"/><group><text variable="volume" font-style="italic"/><text variable="issue" prefix="(" suffix=")"/></group><text variable="page"/></group><text variable="publisher"/><choose><if variable="DOI"><text variable="DOI" prefix="https://doi.org/"/></if><else><text variable="URL"/></else></choose></group></layout>
  </bibliography>
</style>
""";
    public const string EnglishNumeric = """
<?xml version="1.0" encoding="utf-8"?>
<style xmlns="http://purl.org/net/xbiblio/csl" version="1.0" class="in-text" default-locale="en-US">
  <info><title>Английский числовой — базовый шаблон</title><id>urn:ysd:style:en-numeric-v1</id><rights license="https://www.apache.org/licenses/LICENSE-2.0">Apache-2.0; original numeric template, not an official journal style</rights></info>
  <bibliography hanging-indent="true" entry-spacing="1">
    <layout suffix="."><text variable="citation-number" suffix=". "/><group delimiter=". "><names variable="author"><name name-as-sort-order="all" sort-separator=" " initialize-with="" delimiter=", " et-al-min="7" et-al-use-first="6"/></names><text variable="title"/><text variable="container-title"/><group><date variable="issued"><date-part name="year"/></date><text variable="volume" prefix=";"/><text variable="issue" prefix="(" suffix=")"/><text variable="page" prefix=":"/></group><text variable="publisher"/><choose><if variable="DOI"><text variable="DOI" prefix="doi:"/></if><else><text variable="URL"/></else></choose></group></layout>
  </bibliography>
</style>
""";
}
