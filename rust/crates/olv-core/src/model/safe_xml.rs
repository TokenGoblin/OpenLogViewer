//! Ported from `SafeXml.cs`: reading XML that came from somewhere else.
//!
//! Every XML this application parses arrives in a file someone was given: a tune
//! emailed by a tuner, a datalog downloaded from a forum, an .mlg with a tune
//! embedded inside it. None of it can be assumed well-meaning.
//!
//! The danger is entities declared inside the document: ten nested
//! `<!ENTITY b "&a;&a;…">` declarations are a gigabyte from a file of a few hundred
//! bytes. The C# *ignores* the document type definition rather than prohibiting
//! it: prohibiting rejects any file carrying a `<!DOCTYPE>` at all, including the
//! harmless ones some writers emit, while ignoring loads those perfectly and still
//! leaves a bomb's entities undeclared and its parse refused. This does the same:
//! the `<!DOCTYPE …>` is skipped unread, and any reference to an entity beyond the
//! five XML predefines fails the parse. Nothing is fetched from disk or network.
//!
//! The C# returns an `XDocument`; this returns an owned, read-only tree with the
//! parts of it the readers use: elements by local name, attributes, and
//! `XElement.Value`. Comments and processing instructions are dropped (the C#
//! ignores both), and so is whitespace-only text between elements, as
//! `XDocument.Load` drops it, unless `xml:space="preserve"` is in force.

use crate::{Error, Result};
use std::borrow::Cow;

/// A parsed document: one root element.
#[derive(Clone, Debug, PartialEq)]
pub struct XmlDocument {
    root: XmlElement,
}

/// One element. Names are local names; the namespace is kept beside them, since
/// the readers match on `Name.LocalName`.
#[derive(Clone, Debug, PartialEq)]
pub struct XmlElement {
    name: String,
    namespace: Option<String>,
    attributes: Vec<(Option<String>, String, String)>,
    children: Vec<XmlNode>,
}

/// An element's content.
#[derive(Clone, Debug, PartialEq)]
pub enum XmlNode {
    Element(XmlElement),
    Text(String),
}

/// Reads a document, or fails with [`Error::Xml`].
pub fn parse(xml: &str) -> Result<XmlDocument> {
    let text = skip_doctype(xml)?;
    let options = roxmltree::ParsingOptions {
        allow_dtd: false,
        ..roxmltree::ParsingOptions::default()
    };
    let document = roxmltree::Document::parse_with_options(&text, options).map_err(|e| Error::Xml(e.to_string()))?;
    Ok(XmlDocument {
        root: convert(document.root_element(), false),
    })
}

impl XmlDocument {
    /// The root element.
    pub fn root(&self) -> &XmlElement {
        &self.root
    }

    /// `XDocument.Descendants()`: every element in document order, the root first.
    pub fn descendants(&self) -> impl Iterator<Item = &XmlElement> {
        std::iter::once(&self.root).chain(self.root.descendants())
    }
}

impl XmlElement {
    /// `Name.LocalName`.
    pub fn local_name(&self) -> &str {
        &self.name
    }

    /// `Name.NamespaceName`, or `None` for no namespace.
    pub fn namespace(&self) -> Option<&str> {
        self.namespace.as_deref()
    }

    /// `Attribute(name)?.Value`: an attribute in no namespace.
    pub fn attribute(&self, name: &str) -> Option<&str> {
        self.attributes
            .iter()
            .find(|(ns, n, _)| ns.is_none() && n == name)
            .map(|(_, _, v)| v.as_str())
    }

    /// Every attribute as (namespace, local name, value), in document order.
    pub fn attributes(&self) -> impl Iterator<Item = (Option<&str>, &str, &str)> {
        self.attributes
            .iter()
            .map(|(ns, n, v)| (ns.as_deref(), n.as_str(), v.as_str()))
    }

    /// The direct content.
    pub fn children(&self) -> &[XmlNode] {
        &self.children
    }

    /// `Elements()`: the child elements.
    pub fn elements(&self) -> impl Iterator<Item = &XmlElement> {
        self.children.iter().filter_map(|n| match n {
            XmlNode::Element(e) => Some(e),
            XmlNode::Text(_) => None,
        })
    }

    /// `Descendants()`: every element below this one in document order, not
    /// including itself.
    pub fn descendants(&self) -> Descendants<'_> {
        Descendants {
            stack: vec![self.children.iter()],
        }
    }

    /// `XElement.Value`: all the text inside, at any depth, concatenated.
    pub fn value(&self) -> String {
        let mut out = String::new();
        self.collect_text(&mut out);
        out
    }

    fn collect_text(&self, out: &mut String) {
        for child in &self.children {
            match child {
                XmlNode::Text(t) => out.push_str(t),
                XmlNode::Element(e) => e.collect_text(out),
            }
        }
    }
}

/// The iterator behind [`XmlElement::descendants`].
pub struct Descendants<'a> {
    stack: Vec<std::slice::Iter<'a, XmlNode>>,
}

impl<'a> Iterator for Descendants<'a> {
    type Item = &'a XmlElement;

    fn next(&mut self) -> Option<&'a XmlElement> {
        while let Some(top) = self.stack.last_mut() {
            match top.next() {
                Some(XmlNode::Element(e)) => {
                    self.stack.push(e.children.iter());
                    return Some(e);
                }
                Some(XmlNode::Text(_)) => {}
                None => {
                    self.stack.pop();
                }
            }
        }
        None
    }
}

fn convert(node: roxmltree::Node<'_, '_>, preserve_parent: bool) -> XmlElement {
    let preserve = match node.attribute((roxmltree::NS_XML_URI, "space")) {
        Some("preserve") => true,
        Some("default") => false,
        _ => preserve_parent,
    };
    let mut children = Vec::new();
    for child in node.children() {
        if child.is_element() {
            children.push(XmlNode::Element(convert(child, preserve)));
        } else if child.is_text() {
            let text = child.text().unwrap_or("");
            let blank = text.chars().all(|c| matches!(c, ' ' | '\t' | '\r' | '\n'));
            if !text.is_empty() && (preserve || !blank) {
                children.push(XmlNode::Text(text.to_string()));
            }
        }
    }
    XmlElement {
        name: node.tag_name().name().to_string(),
        namespace: node.tag_name().namespace().map(str::to_string),
        attributes: node
            .attributes()
            .map(|a| {
                (
                    a.namespace().map(str::to_string),
                    a.name().to_string(),
                    a.value().to_string(),
                )
            })
            .collect(),
        children,
    }
}

/// Cuts out the `<!DOCTYPE …>` declaration, internal subset and all, without
/// reading it: `DtdProcessing.Ignore`. Only the prolog is searched, since a
/// declaration anywhere else is malformed and the parser will say so.
fn skip_doctype(xml: &str) -> Result<Cow<'_, str>> {
    let bytes = xml.as_bytes();
    let mut i = 0;
    loop {
        while i < bytes.len() && matches!(bytes[i], b' ' | b'\t' | b'\r' | b'\n') {
            i += 1;
        }
        let rest = &xml[i..];
        if rest.starts_with("\u{feff}") {
            i += '\u{feff}'.len_utf8();
        } else if rest.starts_with("<?") {
            match rest.find("?>") {
                Some(end) => i += end + 2,
                None => return Ok(Cow::Borrowed(xml)),
            }
        } else if let Some(comment) = rest.strip_prefix("<!--") {
            match comment.find("-->") {
                Some(end) => i += 4 + end + 3,
                None => return Ok(Cow::Borrowed(xml)),
            }
        } else if rest.starts_with("<!DOCTYPE") {
            break;
        } else {
            return Ok(Cow::Borrowed(xml));
        }
    }

    let start = i;
    let mut j = i + "<!DOCTYPE".len();
    let mut in_subset = false;
    while j < bytes.len() {
        match bytes[j] {
            quote @ (b'"' | b'\'') => {
                j += 1;
                while j < bytes.len() && bytes[j] != quote {
                    j += 1;
                }
            }
            b'<' if xml[j..].starts_with("<!--") => match xml[j + 4..].find("-->") {
                Some(end) => j += 4 + end + 2,
                None => break,
            },
            b'[' => in_subset = true,
            b']' => in_subset = false,
            b'>' if !in_subset => {
                let mut kept = String::with_capacity(xml.len());
                kept.push_str(&xml[..start]);
                kept.push_str(&xml[j + 1..]);
                return Ok(Cow::Owned(kept));
            }
            _ => {}
        }
        j += 1;
    }
    Err(Error::Xml("Unexpected end of file while parsing DOCTYPE.".to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_harmless_doctype_is_read_past() {
        let doc = parse("<?xml version=\"1.0\"?>\n<!DOCTYPE msq [ <!ELEMENT msq ANY> ]>\n<msq a=\"1\"><b>x</b></msq>")
            .unwrap();
        assert_eq!(doc.root().attribute("a"), Some("1"));
        assert_eq!(doc.root().value(), "x");
    }

    #[test]
    fn an_entity_bomb_is_refused_not_expanded() {
        let bomb = "<?xml version=\"1.0\"?>\n<!DOCTYPE lolz [\n <!ENTITY a \"aaaaaaaaaa\">\n <!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">\n]>\n<lolz>&b;</lolz>";
        assert!(matches!(parse(bomb), Err(Error::Xml(_))));
    }

    #[test]
    fn an_external_entity_reaches_nothing() {
        let xxe = "<!DOCTYPE x [ <!ENTITY e SYSTEM \"file:///etc/passwd\"> ]><x>&e;</x>";
        assert!(parse(xxe).is_err());
    }

    #[test]
    fn descendants_are_in_document_order_and_value_concatenates() {
        let doc = parse("<r xmlns=\"urn:x\"><a>1<b>2</b></a>\n  <c>3</c></r>").unwrap();
        let names: Vec<&str> = doc.descendants().map(XmlElement::local_name).collect();
        assert_eq!(names, ["r", "a", "b", "c"]);
        assert_eq!(doc.root().value(), "123");
        assert_eq!(doc.root().namespace(), Some("urn:x"));
    }

    #[test]
    fn malformed_xml_is_an_error() {
        assert!(parse("<msq><not closed").is_err());
    }
}
